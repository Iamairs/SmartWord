using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SmartWord.Application.Context;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Todo;
using SmartWord.Application.Tools;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Core.Telemetry;
using SmartWord.OfficeIntegration.Tools;
using static SmartWord.Application.Orchestration.AgentOrchestratorUtilities;

namespace SmartWord.Application.Orchestration
{
    public sealed partial class AgentOrchestrator
    {
        private async Task<TaskRunRecord> TryStartTaskRunAsync(
            string documentPath,
            string userInput,
            AgentRunOptions options,
            CancellationToken cancellationToken)
        {
            if (_taskHistoryStore == null)
            {
                return null;
            }

            try
            {
                return await _taskHistoryStore
                    .StartRunAsync(
                        new TaskRunStartRequest
                        {
                            DocumentPath = documentPath,
                            UserGoal = userInput ?? string.Empty,
                            Mode = ToTaskHistoryMode(options == null ? AgentMode.Ask : options.Mode),
                            PermissionMode = ToTaskHistoryPermissionMode(ResolvePermissionMode(options)),
                            Model = options == null ? string.Empty : options.Model ?? string.Empty,
                            TotalSteps = ResolveTotalSteps(options)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "创建任务历史运行记录失败，主流程将继续。");
                return null;
            }
        }

        private async Task RecordTaskTelemetryAsync(
            string eventType,
            AgentRunOptions options,
            Dictionary<string, object> data,
            CancellationToken cancellationToken)
        {
            await RecordTelemetryAsync(eventType, options, data, cancellationToken).ConfigureAwait(false);
        }

        private async Task RecordToolTelemetryAsync(
            string eventType,
            AgentRunOptions options,
            ToolCall toolCall,
            ToolCallResult result,
            string operationDescription,
            bool requiresConfirmation,
            bool wasConfirmed,
            long durationMs,
            CancellationToken cancellationToken)
        {
            if (toolCall == null)
            {
                return;
            }

            var data = new Dictionary<string, object>
            {
                ["toolCallId"] = toolCall.Id ?? string.Empty,
                ["toolName"] = toolCall.Name ?? string.Empty,
                ["rawInput"] = toolCall.Input ?? string.Empty,
                ["operationDescription"] = string.IsNullOrWhiteSpace(operationDescription)
                    ? toolCall.Description ?? string.Empty
                    : operationDescription,
                ["completedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["durationMs"] = durationMs,
                ["success"] = result != null && result.Success,
                ["failureType"] = result == null || result.Success ? string.Empty : ClassifyToolFailure(result.Output),
                ["errorMessage"] = result == null || result.Success ? string.Empty : result.Output ?? string.Empty,
                ["affectedParagraphs"] = result == null ? null : result.AffectedParagraphs,
                ["paragraphRefs"] = result == null ? null : result.ParagraphRefs,
                ["outputSizeChars"] = result == null || result.Output == null ? 0 : result.Output.Length,
                ["requiresConfirmation"] = requiresConfirmation,
                ["wasConfirmed"] = wasConfirmed,
                ["isSafetyBlock"] = result != null
                    && !result.Success
                    && (result.Output ?? string.Empty).IndexOf("PERMISSION DENIED", StringComparison.OrdinalIgnoreCase) >= 0
            };

            await RecordTelemetryAsync(eventType, options, data, cancellationToken).ConfigureAwait(false);
        }

        private async Task RecordVerificationTelemetryAsync(
            string eventType,
            AgentRunOptions options,
            ToolCall toolCall,
            ToolCallResult result,
            string operationDescription,
            CancellationToken cancellationToken)
        {
            if (toolCall == null)
            {
                return;
            }

            await RecordTelemetryAsync(
                    eventType,
                    options,
                    new Dictionary<string, object>
                    {
                        ["verificationId"] = toolCall.Id ?? Guid.NewGuid().ToString("N"),
                        ["toolCallId"] = toolCall.Id ?? string.Empty,
                        ["toolName"] = toolCall.Name ?? string.Empty,
                        ["operationDescription"] = operationDescription ?? string.Empty,
                        ["success"] = result != null && result.Success && IsVerificationPassed(result),
                        ["failureReason"] = result == null || result.Success ? string.Empty : result.Output ?? string.Empty,
                        ["checksJson"] = result == null ? string.Empty : result.Output ?? string.Empty,
                        ["completedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task RecordTelemetryAsync(
            string eventType,
            AgentRunOptions options,
            Dictionary<string, object> data,
            CancellationToken cancellationToken)
        {
            if (_telemetrySink == null)
            {
                return;
            }

            try
            {
                var telemetryEvent = AgentTelemetryEvent.Create(eventType);
                var context = AgentTelemetryScope.Current;
                if (context != null)
                {
                    telemetryEvent.EvalRunId = context.EvalRunId;
                    telemetryEvent.TaskRunId = context.TaskRunId;
                    telemetryEvent.CaseId = context.CaseId;
                    telemetryEvent.Level = context.Level;
                    telemetryEvent.Variant = context.Variant;
                }

                telemetryEvent.Mode = options == null ? string.Empty : options.Mode.ToString();
                telemetryEvent.PermissionMode = ResolvePermissionMode(options).ToString();
                telemetryEvent.Model = options == null ? string.Empty : options.Model ?? string.Empty;
                telemetryEvent.Data = data ?? new Dictionary<string, object>();

                await _telemetrySink.RecordAsync(telemetryEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "写入 Agent 评测 Telemetry 失败。EventType={EventType}", eventType);
            }
        }

        private static bool IsVerificationPassed(ToolCallResult result)
        {
            if (result == null || !result.Success)
            {
                return false;
            }

            return !TryGetVerificationAllPassed(result.Output, out var allPassed) || allPassed;
        }

        private static string ClassifyToolFailure(string output)
        {
            var text = output ?? string.Empty;
            if (text.IndexOf("PERMISSION DENIED", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "permission_denied";
            }

            if (text.IndexOf("SKIPPED", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "user_rejected";
            }

            if (text.IndexOf("超时", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "timeout";
            }

            if (text.IndexOf("验证", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("verify", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "verification_failed";
            }

            if (text.IndexOf("参数", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("JSON", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "invalid_arguments";
            }

            if (text.IndexOf("COM", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Word", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "word_com_error";
            }

            return "unknown_error";
        }

        private static string ResolveFailureType(TodoBoardRunOutcome outcome)
        {
            switch (outcome)
            {
                case TodoBoardRunOutcome.Cancelled:
                    return "cancelled";
                case TodoBoardRunOutcome.RolledBack:
                    return "verification_failed";
                case TodoBoardRunOutcome.Failed:
                    return "unknown_error";
                default:
                    return string.Empty;
            }
        }

    }
}
