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
    internal static class AgentEventFactory
    {
        internal static AgentEvent CreateTodoBoardEvent(
            AgentEventType eventType,
            TodoToolMetadata metadata,
            TodoBoardUpdateKind updateKind,
            string message)
        {
            return new AgentEvent
            {
                Type = eventType,
                Message = message ?? string.Empty,
                BoardJson = metadata == null ? string.Empty : metadata.BoardJson ?? string.Empty,
                CurrentTodoId = metadata == null ? string.Empty : metadata.CurrentTodoId ?? string.Empty,
                CompletedSteps = metadata == null ? 0 : metadata.CompletedSteps,
                TotalSteps = metadata == null ? 0 : metadata.TotalSteps,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(updateKind)
            };
        }

        internal static AgentEvent CreateTodoBoardReadyEvent(TodoBoard board, TodoManager todoManager, string message)
        {
            var stats = todoManager == null ? new TodoBoardStats() : todoManager.BuildStats(board);
            return new AgentEvent
            {
                Type = AgentEventType.TodoBoardReady,
                Message = message ?? "当前 Todo Board 已就绪。",
                BoardJson = todoManager == null ? string.Empty : todoManager.SerializeBoard(board),
                CurrentTodoId = stats.CurrentTodoId,
                CompletedSteps = stats.HandledCount,
                TotalSteps = stats.TotalCount,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(TodoBoardUpdateKind.Ready)
            };
        }

        internal static AgentEvent CreateTodoBoardSnapshotEvent(
            AgentEventType eventType,
            TodoBoard board,
            TodoManager todoManager,
            TodoBoardUpdateKind updateKind,
            string message)
        {
            var stats = todoManager == null ? new TodoBoardStats() : todoManager.BuildStats(board);
            return new AgentEvent
            {
                Type = eventType,
                Message = message ?? string.Empty,
                BoardJson = board == null || todoManager == null ? string.Empty : todoManager.SerializeBoard(board),
                CurrentTodoId = stats.CurrentTodoId,
                CompletedSteps = stats.HandledCount,
                TotalSteps = stats.TotalCount,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(updateKind)
            };
        }

        internal static AgentEvent CreateTodoBoardRecoveryRequiredEvent(
            TodoBoardPreparationResult prepareResult,
            TodoManager todoManager,
            string recoveryRequestId)
        {
            var board = prepareResult == null ? null : prepareResult.Board;
            var stats = todoManager == null ? new TodoBoardStats() : todoManager.BuildStats(board);
            return new AgentEvent
            {
                Type = AgentEventType.TodoBoardRecoveryRequired,
                BoardJson = board == null || todoManager == null ? string.Empty : todoManager.SerializeBoard(board),
                CurrentTodoId = stats.CurrentTodoId,
                CompletedSteps = stats.HandledCount,
                TotalSteps = stats.TotalCount,
                RecoveryRequestId = recoveryRequestId ?? string.Empty,
                RecoveryReason = prepareResult == null ? string.Empty : prepareResult.RecoveryReason,
                LastRunOutcome = prepareResult == null ? string.Empty : prepareResult.LastRunOutcome.ToString(),
                LastErrorSummary = prepareResult == null ? string.Empty : prepareResult.LastErrorSummary,
                HasActivePlan = prepareResult != null && prepareResult.HasActivePlan,
                CanRecoverExisting = prepareResult == null || prepareResult.CanRecoverExisting,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(TodoBoardUpdateKind.RecoverySnapshot)
            };
        }

        internal static AgentEvent CreateTodoBoardPausedEvent(
            TodoBoardPreparationResult prepareResult,
            TodoManager todoManager,
            string recoveryRequestId)
        {
            var board = prepareResult == null ? null : prepareResult.Board;
            var stats = todoManager == null ? new TodoBoardStats() : todoManager.BuildStats(board);
            return new AgentEvent
            {
                Type = AgentEventType.TodoBoardPaused,
                Message = prepareResult == null
                    ? "当前任务已暂停。"
                    : string.IsNullOrWhiteSpace(prepareResult.PauseReason)
                        ? "当前任务已暂停。"
                        : prepareResult.PauseReason,
                BoardJson = board == null || todoManager == null ? string.Empty : todoManager.SerializeBoard(board),
                CurrentTodoId = stats.CurrentTodoId,
                CompletedSteps = stats.HandledCount,
                TotalSteps = stats.TotalCount,
                RecoveryRequestId = recoveryRequestId ?? string.Empty,
                LastRunOutcome = prepareResult == null ? string.Empty : prepareResult.LastRunOutcome.ToString(),
                LastErrorSummary = prepareResult == null ? string.Empty : prepareResult.LastErrorSummary,
                HasActivePlan = prepareResult != null && prepareResult.HasActivePlan,
                CanRecoverExisting = prepareResult == null || prepareResult.CanRecoverExisting,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(TodoBoardUpdateKind.PausedSnapshot)
            };
        }

        internal static AgentEvent CreateTodoBoardPausedEvent(
            TodoBoard board,
            TodoManager todoManager,
            string message)
        {
            var stats = todoManager == null ? new TodoBoardStats() : todoManager.BuildStats(board);
            return new AgentEvent
            {
                Type = AgentEventType.TodoBoardPaused,
                Message = message ?? "当前任务已暂停。",
                BoardJson = board == null || todoManager == null ? string.Empty : todoManager.SerializeBoard(board),
                CurrentTodoId = stats.CurrentTodoId,
                CompletedSteps = stats.HandledCount,
                TotalSteps = stats.TotalCount,
                LastRunOutcome = board == null ? string.Empty : board.LastRunOutcome.ToString(),
                LastErrorSummary = board == null ? string.Empty : board.LastErrorSummary,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(TodoBoardUpdateKind.PausedSnapshot)
            };
        }

        internal static string ToTodoBoardUpdateKindValue(TodoBoardUpdateKind updateKind)
        {
            switch (updateKind)
            {
                case TodoBoardUpdateKind.Ready:
                    return "ready";
                case TodoBoardUpdateKind.ToolReadSync:
                    return "tool_read_sync";
                case TodoBoardUpdateKind.ToolWriteSync:
                    return "tool_write_sync";
                case TodoBoardUpdateKind.RollbackRestored:
                    return "rollback_restored";
                case TodoBoardUpdateKind.Reminder:
                    return "reminder";
                case TodoBoardUpdateKind.PausedSnapshot:
                    return "paused_snapshot";
                case TodoBoardUpdateKind.RecoverySnapshot:
                    return "recovery_snapshot";
                case TodoBoardUpdateKind.Unknown:
                default:
                    return "unknown";
            }
        }

        internal static AgentEvent CreateMaxIterationsReachedEvent(
            AgentMode mode,
            int maxIterations,
            string message)
        {
            return new AgentEvent
            {
                Type = AgentEventType.MaxIterationsReached,
                Message = string.IsNullOrWhiteSpace(message)
                    ? BuildMaxIterationsMessage(mode, maxIterations)
                    : message
            };
        }

        internal static string BuildMaxIterationsMessage(AgentMode mode, int maxIterations)
        {
            switch (mode)
            {
                case AgentMode.Agent:
                    return $"当前任务已达到本轮 {maxIterations} 轮预算上限，系统已暂停并保留 Todo Board。你可以继续尝试、跳过当前步骤，或停止本次任务。";
                case AgentMode.Plan:
                    return $"当前规划已达到本轮 {maxIterations} 轮预算上限，但尚未生成最终计划。你可以继续补充信息后再次规划。";
                case AgentMode.Ask:
                default:
                    return $"当前回答已达到本轮 {maxIterations} 轮预算上限，若仍需继续，可直接继续追问。";
            }
        }

        internal static AgentEvent CreateToolStartedEvent(ToolCall toolCall, string operationDescription)
        {
            return new AgentEvent
            {
                Type = AgentEventType.ToolCallStarted,
                ToolCallId = toolCall == null ? string.Empty : toolCall.Id,
                ToolName = toolCall == null ? string.Empty : toolCall.Name,
                ToolInput = toolCall == null ? string.Empty : toolCall.Input ?? string.Empty,
                RequiresConfirmation = false,
                OperationDescription = operationDescription ?? string.Empty
            };
        }

        internal static AgentEvent CreateToolCompletedEvent(ToolCall toolCall, ToolCallResult result)
{
    return new AgentEvent
    {
        Type = AgentEventType.ToolCallCompleted,
        ToolCallId = toolCall.Id,
        ToolName = toolCall.Name,
        ToolInput = toolCall.Input ?? string.Empty,
        ToolOutput = result.Output ?? string.Empty,
        ToolSuccess = result.Success,
        ParagraphRefs = result.ParagraphRefs,
        AffectedParagraphs = result.AffectedParagraphs,
        OperationDescription = result.OperationDescription ?? string.Empty
    };
}

        internal static string DecorateToolOutput(
            string toolName,
            string output,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var trimmedOutput = output.TrimStart();
            if (!trimmedOutput.StartsWith("{", StringComparison.Ordinal)
                && !trimmedOutput.StartsWith("[", StringComparison.Ordinal))
            {
                return output;
            }

            try
            {
                var token = JToken.Parse(output);
                var rootObject = token as JObject;
                var discovered = new JArray();

                switch (toolName ?? string.Empty)
                {
                    case "read_section":
                        AttachRefsOnArray(
                            token["paragraphs"] as JArray,
                            "index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                    case "grep_document":
                        AttachRefsOnArray(
                            token["results"] as JArray,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        foreach (var item in (token["results"] as JArray ?? new JArray()).OfType<JObject>())
                        {
                            AttachRefsOnArray(
                                item["context_before"] as JArray,
                                "index",
                                "text",
                                documentPath,
                                citationRegistry,
                                paragraphToRef,
                                ref nextCitationRef,
                                discovered);
                            AttachRefsOnArray(
                                item["context_after"] as JArray,
                                "index",
                                "text",
                                documentPath,
                                citationRegistry,
                                paragraphToRef,
                                ref nextCitationRef,
                                discovered);
                        }
                        break;
                    case "probe_document":
                        AttachRefsOnArray(
                            token["outline"] as JArray,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        AttachRefOnObject(
                            token["selection"] as JObject,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                    case "get_selection_context":
                        AttachRefOnObject(
                            token["selection"] as JObject,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        AttachRefOnObject(
                            token["context"] as JObject,
                            "paragraph_index",
                            "paragraph_full",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                    case "read_annotations":
                        AttachRefsOnArray(
                            token["results"] as JArray,
                            "para_index",
                            "anchor_text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                }

                if (discovered.Count > 0 && rootObject != null)
                {
                    rootObject["citation_entries"] = discovered;
                }

                return (rootObject ?? token).ToString(Formatting.None);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "装饰工具输出的引用映射失败。ToolName={ToolName}", toolName);
                return output;
            }
        }

        internal static void AttachRefsOnArray(
            JArray array,
            string paragraphPropertyName,
            string excerptPropertyName,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef,
            JArray discovered)
        {
            if (array == null)
            {
                return;
            }

            foreach (var item in array.OfType<JObject>())
            {
                AttachRefOnObject(
                    item,
                    paragraphPropertyName,
                    excerptPropertyName,
                    documentPath,
                    citationRegistry,
                    paragraphToRef,
                    ref nextCitationRef,
                    discovered);
            }
        }

        internal static void AttachRefOnObject(
            JObject target,
            string paragraphPropertyName,
            string excerptPropertyName,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef,
            JArray discovered)
        {
            if (target == null)
            {
                return;
            }

            var paragraphIndex = target.Value<int?>(paragraphPropertyName);
            if (!paragraphIndex.HasValue || paragraphIndex.Value < 0)
            {
                return;
            }

            var excerpt = target.Value<string>(excerptPropertyName) ?? string.Empty;
            var refId = RegisterCitation(
                paragraphIndex.Value,
                excerpt,
                documentPath,
                citationRegistry,
                paragraphToRef,
                ref nextCitationRef);
            target["ref"] = refId;
            discovered.Add(new JObject
            {
                ["ref"] = refId,
                ["paragraphIndex"] = paragraphIndex.Value,
                ["excerpt"] = excerpt
            });
        }

        internal static int RegisterCitation(
            int paragraphIndex,
            string excerpt,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef)
        {
            if (paragraphToRef.TryGetValue(paragraphIndex, out var existingRef))
            {
                return existingRef;
            }

            var refId = nextCitationRef++;
            paragraphToRef[paragraphIndex] = refId;
            citationRegistry[refId] = new CitationEntry
            {
                Ref = refId,
                ParagraphIndex = paragraphIndex,
                Excerpt = excerpt,
                DocumentPath = documentPath
            };

            return refId;
        }

        internal static List<CitationEntry> BuildCitations(
            string assistantContent,
            IReadOnlyDictionary<int, CitationEntry> citationRegistry)
        {
            var citations = new List<CitationEntry>();
            if (string.IsNullOrWhiteSpace(assistantContent) || citationRegistry == null || citationRegistry.Count == 0)
            {
                return citations;
            }

            foreach (Match match in Regex.Matches(assistantContent, @"\[(\d+)\]"))
            {
                if (!int.TryParse(match.Groups[1].Value, out var refId))
                {
                    continue;
                }

                if (!citationRegistry.TryGetValue(refId, out var citation))
                {
                    continue;
                }

                if (citations.Any(item => item.Ref == refId))
                {
                    continue;
                }

                citations.Add(citation);
            }

            return citations;
        }

        internal static void RecordConsecutiveFailure(
            ref int consecutiveFailures,
            ref string lastFailureSummary,
            string toolName,
            string toolOutput)
        {
            consecutiveFailures++;
            lastFailureSummary = BuildFailureSummary(toolName, toolOutput);
        }

        internal static AgentEvent CreateCircuitBreakerEvent(string lastFailureSummary)
        {
            var message = "工具已连续失败 3 次，系统为防止误操作已停止本次任务。";
            if (!string.IsNullOrWhiteSpace(lastFailureSummary))
            {
                message += Environment.NewLine
                    + "最近一次失败大致原因："
                    + lastFailureSummary
                    + Environment.NewLine
                    + "建议：检查任务范围、选区或指令后重新发起；如果是文档状态或权限问题，请先处理对应限制。";
            }

            return new AgentEvent
            {
                Type = AgentEventType.Error,
                Message = message
            };
        }

        internal static string BuildFailureSummary(string toolName, string toolOutput)
        {
            var normalizedToolName = string.IsNullOrWhiteSpace(toolName)
                ? "未知工具"
                : toolName.Trim();
            var normalizedOutput = NormalizeFailureText(toolOutput);
            return string.IsNullOrWhiteSpace(normalizedOutput)
                ? $"工具 {normalizedToolName} 未返回明确错误详情。"
                : $"工具 {normalizedToolName}：{Truncate(normalizedOutput, 240)}";
        }

        internal static string NormalizeFailureText(string toolOutput)
        {
            if (string.IsNullOrWhiteSpace(toolOutput))
            {
                return string.Empty;
            }

            var text = Regex.Replace(toolOutput, @"\[(ERROR in [^\]]+|PERMISSION DENIED|SKIPPED)\]\s*", string.Empty);
            text = Regex.Replace(text, @"Tool '[^']+' (was blocked|is not allowed in current mode|was skipped by user)\.?", string.Empty);
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        internal static bool IsTodoToolName(string toolName)
        {
            return string.Equals(toolName, "todo_read", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "todo_write", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDocumentWriteTool(string toolName)
        {
            return string.Equals(toolName, "patch_range", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "execute_script", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsVerificationTool(string toolName)
        {
            return string.Equals(toolName, "verify_script", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsRepairProbeTool(string toolName)
        {
            return string.Equals(toolName, "read_script", StringComparison.OrdinalIgnoreCase);
        }

    }
}

