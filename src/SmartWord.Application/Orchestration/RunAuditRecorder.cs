using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Serilog;
using SmartWord.Application.Todo;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Core.Telemetry;
using static SmartWord.Application.Orchestration.AgentOrchestratorUtilities;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// 负责任务历史和评测 Telemetry 的旁路记录。
    /// 记录失败只写日志，不得改变 Agent 主流程结果。
    /// </summary>
    internal sealed class RunAuditRecorder
    {
        private readonly ITaskHistoryStore _taskHistoryStore;
        private readonly IAgentTelemetrySink _telemetrySink;

        internal RunAuditRecorder(
            ITaskHistoryStore taskHistoryStore,
            IAgentTelemetrySink telemetrySink)
        {
            _taskHistoryStore = taskHistoryStore;
            _telemetrySink = telemetrySink ?? NullAgentTelemetrySink.Instance;
        }

        internal async Task<TaskRunRecord> TryStartTaskRunAsync(
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

        internal async Task TryRecordTaskToolAsync(
            string taskRunId,
            ToolCall toolCall,
            ToolCallResult result,
            string operationDescription,
            CancellationToken cancellationToken)
        {
            if (_taskHistoryStore == null || string.IsNullOrWhiteSpace(taskRunId) || toolCall == null)
            {
                return;
            }

            try
            {
                await _taskHistoryStore
                    .RecordToolAsync(
                        taskRunId,
                        new TaskToolRecord
                        {
                            ToolCallId = toolCall.Id ?? string.Empty,
                            ToolName = toolCall.Name ?? string.Empty,
                            OperationDescription = string.IsNullOrWhiteSpace(operationDescription)
                                ? toolCall.Description ?? string.Empty
                                : operationDescription,
                            RawInput = toolCall.Input ?? string.Empty,
                            Output = result == null ? string.Empty : result.Output ?? string.Empty,
                            Success = result != null && result.Success,
                            CreatedAtUtc = DateTimeOffset.UtcNow
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "记录任务工具审计失败。TaskRunId={TaskRunId}, ToolName={ToolName}", taskRunId, toolCall.Name);
            }
        }

        internal async Task TryRecordTaskChangeAsync(
            string taskRunId,
            AgentEvent changeEvent,
            string status,
            CancellationToken cancellationToken)
        {
            if (_taskHistoryStore == null || string.IsNullOrWhiteSpace(taskRunId) || changeEvent == null)
            {
                return;
            }

            try
            {
                await _taskHistoryStore
                    .RecordChangeAsync(
                        taskRunId,
                        new TaskChangeRecord
                        {
                            ToolCallId = changeEvent.ToolCallId ?? string.Empty,
                            ToolName = changeEvent.ToolName ?? string.Empty,
                            OperationDescription = changeEvent.OperationDescription ?? string.Empty,
                            AffectedParagraphs = changeEvent.AffectedParagraphs ?? new int[0],
                            Status = status ?? string.Empty,
                            Message = string.IsNullOrWhiteSpace(changeEvent.Message)
                                ? changeEvent.ToolOutput ?? string.Empty
                                : changeEvent.Message,
                            CreatedAtUtc = DateTimeOffset.UtcNow
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "记录任务改动审计失败。TaskRunId={TaskRunId}, Status={Status}", taskRunId, status);
            }
        }

        internal async Task TryCompleteTaskRunAsync(
            TaskRunRecord auditRun,
            TaskRunCompletion completion,
            CancellationToken cancellationToken)
        {
            if (_taskHistoryStore == null || auditRun == null || string.IsNullOrWhiteSpace(auditRun.Id))
            {
                return;
            }

            try
            {
                await _taskHistoryStore
                    .CompleteRunAsync(auditRun.Id, completion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "完成任务历史运行记录失败。TaskRunId={TaskRunId}", auditRun.Id);
            }
        }

        internal Task RecordTaskTelemetryAsync(
            string eventType,
            AgentRunOptions options,
            Dictionary<string, object> data,
            CancellationToken cancellationToken)
        {
            return RecordTelemetryAsync(eventType, options, data, cancellationToken);
        }

        internal async Task RecordToolTelemetryAsync(
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

            await RecordTelemetryAsync(
                    eventType,
                    options,
                    new Dictionary<string, object>
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
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        internal Task RecordVerificationTelemetryAsync(
            string eventType,
            AgentRunOptions options,
            ToolCall toolCall,
            ToolCallResult result,
            string operationDescription,
            CancellationToken cancellationToken)
        {
            if (toolCall == null)
            {
                return Task.CompletedTask;
            }

            return RecordTelemetryAsync(
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
                cancellationToken);
        }

        internal static string ResolveFailureType(TodoBoardRunOutcome outcome)
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

        private async Task RecordTelemetryAsync(
            string eventType,
            AgentRunOptions options,
            Dictionary<string, object> data,
            CancellationToken cancellationToken)
        {
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

            try
            {
                var payload = JObject.Parse(result.Output ?? string.Empty);
                var allPassed = payload["all_passed"];
                return allPassed == null || allPassed.Type == JTokenType.Null || allPassed.Value<bool>();
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static string ClassifyToolFailure(string output)
        {
            var text = output ?? string.Empty;
            if (text.IndexOf("PERMISSION DENIED", StringComparison.OrdinalIgnoreCase) >= 0) return "permission_denied";
            if (text.IndexOf("SKIPPED", StringComparison.OrdinalIgnoreCase) >= 0) return "user_rejected";
            if (text.IndexOf("超时", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0) return "timeout";
            if (text.IndexOf("验证", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("verify", StringComparison.OrdinalIgnoreCase) >= 0) return "verification_failed";
            if (text.IndexOf("参数", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("JSON", StringComparison.OrdinalIgnoreCase) >= 0) return "invalid_arguments";
            if (text.IndexOf("COM", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Word", StringComparison.OrdinalIgnoreCase) >= 0) return "word_com_error";
            return "unknown_error";
        }
    }
}
