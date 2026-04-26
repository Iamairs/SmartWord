using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SmartWord.Core.Enums;
using SmartWord.Core.Models;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 提供最小可用的历史压缩能力：保留 system 消息与最近消息，并插入一条压缩摘要。
    /// </summary>
    public class ConversationCompressor
    {
        private const int DefaultRecentMessageCount = 6;

        public IReadOnlyList<AgentMessage> Compress(IReadOnlyList<AgentMessage> messages)
        {
            return Compress(messages, ConversationCompressionContext.Default);
        }

        public IReadOnlyList<AgentMessage> Compress(
            IReadOnlyList<AgentMessage> messages,
            ConversationCompressionContext context)
        {
            if (messages == null || messages.Count == 0)
            {
                return Array.Empty<AgentMessage>();
            }

            var safeContext = context ?? ConversationCompressionContext.Default;
            var clonedMessages = messages
                .Where(message => message != null)
                .Select(CloneMessage)
                .ToList();
            var systemMessages = clonedMessages
                .Where(message => string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var nonSystemMessages = clonedMessages
                .Where(message => !string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (nonSystemMessages.Count <= DefaultRecentMessageCount)
            {
                return clonedMessages;
            }

            var recentStartIndex = ResolveRecentStartIndex(nonSystemMessages, DefaultRecentMessageCount);
            var compactedMessages = nonSystemMessages
                .Take(recentStartIndex)
                .ToList();
            if (compactedMessages.Count == 0)
            {
                return clonedMessages;
            }

            var recentMessages = nonSystemMessages
                .Skip(recentStartIndex)
                .ToList();
            var protocolSafeRecentMessages = BuildProtocolSafeRecentMessages(recentMessages);
            var lastUserMessage = nonSystemMessages.LastOrDefault(IsRealUserMessage);
            var shouldReinsertUserMessage = lastUserMessage != null
                && !protocolSafeRecentMessages.Any(IsRealUserMessage);

            var result = new List<AgentMessage>(
                systemMessages.Count
                + protocolSafeRecentMessages.Count
                + (shouldReinsertUserMessage ? 2 : 1));
            result.AddRange(systemMessages);
            result.Add(new AgentMessage
            {
                Role = "system",
                Content = BuildSummaryContent(compactedMessages, nonSystemMessages, safeContext),
                IsCompressedSummary = true
            });
            if (shouldReinsertUserMessage)
            {
                result.Add(CloneMessage(lastUserMessage));
            }

            result.AddRange(protocolSafeRecentMessages);
            return result;
        }

        private static int ResolveRecentStartIndex(IReadOnlyList<AgentMessage> nonSystemMessages, int recentMessageCount)
        {
            var startIndex = Math.Max(0, nonSystemMessages.Count - Math.Max(1, recentMessageCount));
            if (startIndex >= nonSystemMessages.Count || !IsToolMessage(nonSystemMessages[startIndex]))
            {
                return startIndex;
            }

            var toolCallId = nonSystemMessages[startIndex].ToolCallId;
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                return startIndex;
            }

            for (var index = startIndex - 1; index >= 0; index--)
            {
                var candidate = nonSystemMessages[index];
                if (IsUserMessage(candidate))
                {
                    break;
                }

                if (IsAssistantMessage(candidate)
                    && candidate.ToolCalls != null
                    && candidate.ToolCalls.Any(toolCall =>
                        toolCall != null
                        && string.Equals(toolCall.Id, toolCallId, StringComparison.Ordinal)))
                {
                    return index;
                }
            }

            return startIndex;
        }

        private static List<AgentMessage> BuildProtocolSafeRecentMessages(IReadOnlyList<AgentMessage> recentMessages)
        {
            var result = new List<AgentMessage>();
            for (var index = 0; index < recentMessages.Count; index++)
            {
                var message = recentMessages[index];
                if (message == null)
                {
                    continue;
                }

                if (IsUserMessage(message) && message.IsInternalObservation)
                {
                    continue;
                }

                if (IsToolMessage(message))
                {
                    // 压缩窗口不能以孤立 tool 结果开头；没有对应 assistant.tool_calls 时会被兼容接口拒绝。
                    continue;
                }

                if (IsAssistantMessage(message)
                    && message.ToolCalls != null
                    && message.ToolCalls.Count > 0)
                {
                    if (message.ToolCalls.Any(IsInternalVerificationToolCall))
                    {
                        var plainAssistantMessage = CloneMessage(message);
                        plainAssistantMessage.ToolCalls = new List<ToolCall>();
                        if (!string.IsNullOrWhiteSpace(plainAssistantMessage.Content))
                        {
                            result.Add(plainAssistantMessage);
                        }

                        index = SkipFollowingToolMessages(recentMessages, index + 1) - 1;
                        continue;
                    }

                    var requiredToolCallIds = message.ToolCalls
                        .Where(toolCall => toolCall != null
                            && !string.IsNullOrWhiteSpace(toolCall.Id)
                            && !string.IsNullOrWhiteSpace(toolCall.Name))
                        .Select(toolCall => toolCall.Id)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    var toolResults = new List<AgentMessage>();
                    var observedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
                    var cursor = index + 1;

                    while (cursor < recentMessages.Count && IsToolMessage(recentMessages[cursor]))
                    {
                        var toolMessage = recentMessages[cursor];
                        if (!string.IsNullOrWhiteSpace(toolMessage.ToolCallId)
                            && requiredToolCallIds.Contains(toolMessage.ToolCallId)
                            && observedToolCallIds.Add(toolMessage.ToolCallId))
                        {
                            toolResults.Add(CloneMessage(toolMessage));
                        }

                        cursor++;
                    }

                    if (requiredToolCallIds.Count > 0
                        && observedToolCallIds.Count == requiredToolCallIds.Count)
                    {
                        result.Add(CloneMessage(message));
                        result.AddRange(toolResults);
                    }
                    else
                    {
                        var plainAssistantMessage = CloneMessage(message);
                        plainAssistantMessage.ToolCalls = new List<ToolCall>();
                        if (!string.IsNullOrWhiteSpace(plainAssistantMessage.Content))
                        {
                            result.Add(plainAssistantMessage);
                        }
                    }

                    index = cursor - 1;
                    continue;
                }

                result.Add(CloneMessage(message));
            }

            return result;
        }

        private static int SkipFollowingToolMessages(IReadOnlyList<AgentMessage> messages, int startIndex)
        {
            var index = startIndex;
            while (index < messages.Count && IsToolMessage(messages[index]))
            {
                index++;
            }

            return index;
        }

        private static string BuildSummaryContent(
            IReadOnlyList<AgentMessage> compactedMessages,
            IReadOnlyList<AgentMessage> allNonSystemMessages,
            ConversationCompressionContext context)
        {
            var userCount = compactedMessages.Count(message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
            var assistantCount = compactedMessages.Count(message =>
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
            var toolCount = compactedMessages.Count(message =>
                string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase));
            var lastUserMessage = compactedMessages.LastOrDefault(IsRealUserMessage)
                ?? allNonSystemMessages.LastOrDefault(IsRealUserMessage);
            var lastAssistantMessage = compactedMessages.LastOrDefault(message =>
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && !message.IsCompressedSummary);
            var lastToolMessage = compactedMessages.LastOrDefault(message =>
                string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase));

            var builder = new StringBuilder();
            builder.AppendLine("[压缩上下文] 较早消息已压缩，请把以下状态视为当前任务记忆。");
            builder.AppendLine("模式：" + ResolveModeLabel(context.Mode));
            if (!string.IsNullOrWhiteSpace(context.DocumentPath))
            {
                builder.AppendLine("当前文档：" + SummarizeText(context.DocumentPath, 180));
            }

            builder.AppendLine("已压缩消息数：" + compactedMessages.Count);
            builder.AppendLine("用户消息数：" + userCount);
            builder.AppendLine("助手消息数：" + assistantCount);
            builder.AppendLine("工具消息数：" + toolCount);

            var userGoal = !string.IsNullOrWhiteSpace(context.CurrentUserGoal)
                ? context.CurrentUserGoal
                : lastUserMessage == null ? string.Empty : lastUserMessage.Content;
            if (!string.IsNullOrWhiteSpace(userGoal))
            {
                builder.AppendLine("最近真实用户目标：" + SummarizeText(userGoal, 180));
            }

            AppendModeSpecificSummary(builder, compactedMessages, allNonSystemMessages, context);

            if (lastAssistantMessage != null)
            {
                builder.AppendLine("最近助手回复：" + SummarizeText(lastAssistantMessage.Content, 160));
            }

            if (lastToolMessage != null)
            {
                builder.AppendLine("最近工具结果：" + BuildToolResultSummary(lastToolMessage));
            }

            return builder.ToString().Trim();
        }

        private static void AppendModeSpecificSummary(
            StringBuilder builder,
            IReadOnlyList<AgentMessage> compactedMessages,
            IReadOnlyList<AgentMessage> allNonSystemMessages,
            ConversationCompressionContext context)
        {
            switch (context.Mode)
            {
                case AgentMode.Plan:
                    AppendPlanSummary(builder, compactedMessages, context);
                    break;
                case AgentMode.Agent:
                    AppendAgentSummary(builder, compactedMessages, allNonSystemMessages, context);
                    break;
                case AgentMode.Ask:
                default:
                    AppendAskSummary(builder, compactedMessages, context);
                    break;
            }
        }

        private static void AppendAskSummary(
            StringBuilder builder,
            IReadOnlyList<AgentMessage> compactedMessages,
            ConversationCompressionContext context)
        {
            builder.AppendLine("[Ask 状态]");
            AppendDocumentSnapshot(builder, context);
            var evidenceSummaries = compactedMessages
                .Where(IsToolMessage)
                .Where(message => IsReadEvidenceTool(message.ToolName))
                .Reverse()
                .Take(3)
                .Select(BuildToolResultSummary)
                .Reverse()
                .ToList();
            if (evidenceSummaries.Count == 0)
            {
                builder.AppendLine("- 已读取证据：压缩历史中未保留明确证据摘要；如问题依赖文档证据，请使用最窄读取工具补充。");
            }
            else
            {
                builder.AppendLine("- 已读取证据：" + string.Join("；", evidenceSummaries));
            }

            builder.AppendLine("- 引用约束：回答如需引用文档证据，必须基于已有 ref 或重新读取必要片段。");
        }

        private static void AppendPlanSummary(
            StringBuilder builder,
            IReadOnlyList<AgentMessage> compactedMessages,
            ConversationCompressionContext context)
        {
            builder.AppendLine("[Plan 状态]");
            if (context.ActivePlan != null)
            {
                if (!string.IsNullOrWhiteSpace(context.ActivePlan.TaskDescription))
                {
                    builder.AppendLine("- 当前计划目标：" + SummarizeText(context.ActivePlan.TaskDescription, 180));
                }

                var todos = context.ActivePlan.TodoList == null
                    ? new List<TodoItem>()
                    : context.ActivePlan.TodoList.Take(5).ToList();
                if (todos.Count > 0)
                {
                    builder.AppendLine("- 当前计划条目：" + string.Join("；", todos.Select(item => SummarizeText(item.Description, 80))));
                }
            }

            var questionAnswers = compactedMessages
                .Where(IsRealUserMessage)
                .Reverse()
                .Take(3)
                .Select(message => SummarizeText(message.Content, 100))
                .Reverse()
                .ToList();
            if (questionAnswers.Count > 0)
            {
                builder.AppendLine("- 最近用户确认：" + string.Join("；", questionAnswers));
            }

            builder.AppendLine("- 下一步：信息足够时输出计划；若仍缺关键限制，只继续询问必要问题。");
        }

        private static void AppendAgentSummary(
            StringBuilder builder,
            IReadOnlyList<AgentMessage> compactedMessages,
            IReadOnlyList<AgentMessage> allNonSystemMessages,
            ConversationCompressionContext context)
        {
            builder.AppendLine("[Agent 执行状态]");
            AppendTodoBoardSummary(builder, context.CurrentTodoBoard);
            AppendActivePlanFallback(builder, context.ActivePlan);
            AppendPendingWriteSummary(builder, context.PendingWriteStep);
            AppendAutoVerifySummary(builder, compactedMessages, allNonSystemMessages, context);
            builder.AppendLine("- 禁止事项：不要重复已验证提交的写入；不要直接调用 verify_script；写工具需要提供可执行的 verify_code 或使用可自动验证的 patch_range。");
        }

        private static void AppendDocumentSnapshot(StringBuilder builder, ConversationCompressionContext context)
        {
            var document = context.DocumentContext;
            if (document == null)
            {
                return;
            }

            builder.AppendLine(
                "- 文档快照：段落="
                + document.ParagraphCount
                + "，表格="
                + document.TableCount
                + "，批注="
                + document.AnnotationCount
                + "，当前段落="
                + document.CursorParagraphIndex);
        }

        private static void AppendTodoBoardSummary(StringBuilder builder, TodoBoard board)
        {
            if (board == null || board.Items == null || board.Items.Count == 0)
            {
                builder.AppendLine("- Todo Board：当前未启用或为空；这在简单任务中是允许的，请依据用户目标、写入状态和自动验证结果继续。");
                return;
            }

            builder.AppendLine("- Todo Board 状态：" + board.ExecutionState + "，最近结果=" + board.LastRunOutcome);
            var current = board.Items
                .OrderBy(item => item.Order)
                .FirstOrDefault(item => item.Status == TodoItemStatus.InProgress)
                ?? board.Items.OrderBy(item => item.Order).FirstOrDefault(item => item.Status == TodoItemStatus.Pending);
            if (current != null)
            {
                builder.AppendLine("- 当前 Todo：" + current.Id + " " + SummarizeText(current.Content, 120));
            }

            var completed = board.Items
                .Where(item => item.Status == TodoItemStatus.Completed)
                .OrderBy(item => item.Order)
                .Take(3)
                .Select(item => item.Id + " " + SummarizeText(item.Content, 70))
                .ToList();
            if (completed.Count > 0)
            {
                builder.AppendLine("- 已完成 Todo：" + string.Join("；", completed));
            }

            var failed = board.Items
                .Where(item => item.Status == TodoItemStatus.Failed)
                .OrderBy(item => item.Order)
                .Take(3)
                .Select(item => item.Id + " " + SummarizeText(item.Content, 70))
                .ToList();
            if (failed.Count > 0)
            {
                builder.AppendLine("- 失败 Todo：" + string.Join("；", failed));
            }
        }

        private static void AppendActivePlanFallback(StringBuilder builder, ExecutionPlan activePlan)
        {
            if (activePlan == null || activePlan.TodoList == null || activePlan.TodoList.Count == 0)
            {
                return;
            }

            builder.AppendLine("- ActivePlan 可作为任务板为空时的备用计划：" + string.Join(
                "；",
                activePlan.TodoList.Take(4).Select(item => SummarizeText(item.Description, 70))));
        }

        private static void AppendPendingWriteSummary(StringBuilder builder, PendingWriteStepSnapshot pendingWriteStep)
        {
            if (pendingWriteStep == null)
            {
                builder.AppendLine("- 当前写步骤：无待验证或待修复写步骤。");
                return;
            }

            builder.AppendLine("- 当前写步骤：" + SummarizeText(pendingWriteStep.OperationDescription, 140));
            builder.AppendLine("- 写步骤状态：" + pendingWriteStep.State + "，修复次数=" + pendingWriteStep.RepairAttempts);
            if (pendingWriteStep.AffectedParagraphs != null && pendingWriteStep.AffectedParagraphs.Length > 0)
            {
                builder.AppendLine("- 影响段落：" + string.Join(",", pendingWriteStep.AffectedParagraphs.Take(12)));
            }

            if (!string.IsNullOrWhiteSpace(pendingWriteStep.LastFailureMessage))
            {
                builder.AppendLine("- 最近失败：" + SummarizeText(pendingWriteStep.LastFailureMessage, 220));
            }

            if (!string.IsNullOrWhiteSpace(pendingWriteStep.VerificationOperationDescription))
            {
                builder.AppendLine("- 自动验证计划：" + SummarizeText(pendingWriteStep.VerificationOperationDescription, 160));
            }
        }

        private static void AppendAutoVerifySummary(
            StringBuilder builder,
            IReadOnlyList<AgentMessage> compactedMessages,
            IReadOnlyList<AgentMessage> allNonSystemMessages,
            ConversationCompressionContext context)
        {
            var observations = new List<AgentMessage>();
            if (context.RecentInternalObservations != null)
            {
                observations.AddRange(context.RecentInternalObservations.Where(IsAutoVerifyObservation).Select(CloneMessage));
            }

            observations.AddRange(allNonSystemMessages.Where(IsAutoVerifyObservation).Select(CloneMessage));
            var distinctObservations = observations
                .Where(message => !string.IsNullOrWhiteSpace(message.Content))
                .GroupBy(message => message.Content)
                .Select(group => group.First())
                .Reverse()
                .Take(3)
                .Reverse()
                .ToList();

            if (distinctObservations.Count == 0)
            {
                builder.AppendLine("- 最近自动验证：压缩历史中没有自动验证观察。");
                return;
            }

            builder.AppendLine("- 最近自动验证：" + string.Join("；", distinctObservations.Select(message => SummarizeAutoVerifyObservation(message.Content))));
        }

        private static string SummarizeAutoVerifyObservation(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "empty";
            }

            var normalized = NormalizeWhitespace(content);
            if (normalized.Contains("已自动验证通过且已提交"))
            {
                return "已验证提交：" + SummarizeText(normalized, 180);
            }

            if (normalized.Contains("已回退") || normalized.Contains("回退"))
            {
                return "验证失败并回滚：" + SummarizeText(normalized, 220);
            }

            return SummarizeText(normalized, 220);
        }

        private static string BuildToolResultSummary(AgentMessage toolMessage)
        {
            var toolName = string.IsNullOrWhiteSpace(toolMessage.ToolName) ? toolMessage.Name : toolMessage.ToolName;
            if (string.IsNullOrWhiteSpace(toolName))
            {
                toolName = "unknown";
            }

            var prefix = toolName + " success=" + toolMessage.ToolSuccess;
            if (string.Equals(toolName, "verify_script", StringComparison.OrdinalIgnoreCase))
            {
                return prefix + "（旧历史中的内部验证记录，当前版本不允许模型直接调用 verify_script）";
            }

            if (string.IsNullOrWhiteSpace(toolMessage.Content))
            {
                return prefix;
            }

            try
            {
                var token = JToken.Parse(toolMessage.Content);
                if (string.Equals(toolName, "patch_range", StringComparison.OrdinalIgnoreCase))
                {
                    return prefix
                        + " applied=" + token.Value<int?>("applied")
                        + " failed=" + token.Value<int?>("failed")
                        + " affected=" + SummarizeArray(token["affected_paragraphs"]);
                }

                if (string.Equals(toolName, "execute_script", StringComparison.OrdinalIgnoreCase))
                {
                    return prefix + " output=" + SummarizeText(token.ToString(Newtonsoft.Json.Formatting.None), 160);
                }

                if (IsReadEvidenceTool(toolName))
                {
                    return prefix + " evidence=" + SummarizeText(token.ToString(Newtonsoft.Json.Formatting.None), 160);
                }

                if (IsTodoToolName(toolName))
                {
                    return prefix + " todo=" + SummarizeText(token.ToString(Newtonsoft.Json.Formatting.None), 160);
                }
            }
            catch
            {
                // 工具输出不一定是 JSON，摘要时回退为安全截断文本。
            }

            return prefix + " output=" + SummarizeText(toolMessage.Content, 160);
        }

        private static string SummarizeArray(JToken token)
        {
            if (!(token is JArray array) || array.Count == 0)
            {
                return "none";
            }

            return string.Join(",", array.Take(12).Select(item => item.ToString()));
        }

        private static bool IsReadEvidenceTool(string toolName)
        {
            return string.Equals(toolName, "probe_document", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "read_section", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "grep_document", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "get_selection_context", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "read_table", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "read_annotations", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "read_script", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTodoToolName(string toolName)
        {
            return string.Equals(toolName, "todo_read", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "todo_write", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveModeLabel(AgentMode mode)
        {
            switch (mode)
            {
                case AgentMode.Plan:
                    return "Plan";
                case AgentMode.Agent:
                    return "Agent";
                case AgentMode.Ask:
                default:
                    return "Ask";
            }
        }

        private static string SummarizeText(string text)
        {
            return SummarizeText(text, 120);
        }

        private static string SummarizeText(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "empty";
            }

            var normalized = NormalizeWhitespace(text);
            return normalized.Length <= maxLength
                ? normalized
                : normalized.Substring(0, maxLength) + "...";
        }

        private static string NormalizeWhitespace(string text)
        {
            return (text ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }

        private static bool IsUserMessage(AgentMessage message)
        {
            return message != null
                && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRealUserMessage(AgentMessage message)
        {
            return IsUserMessage(message) && !message.IsInternalObservation;
        }

        private static bool IsAutoVerifyObservation(AgentMessage message)
        {
            return IsUserMessage(message)
                && message.IsInternalObservation
                && string.Equals(message.InternalObservationKind, "auto_verify_result", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInternalVerificationToolCall(ToolCall toolCall)
        {
            return toolCall != null
                && string.Equals(toolCall.Name, "verify_script", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAssistantMessage(AgentMessage message)
        {
            return message != null
                && string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsToolMessage(AgentMessage message)
        {
            return message != null
                && string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase);
        }

        private static AgentMessage CloneMessage(AgentMessage message)
        {
            return new AgentMessage
            {
                Role = message.Role,
                Content = message.Content,
                ReasoningContent = message.ReasoningContent,
                ToolCalls = message.ToolCalls == null
                    ? new List<ToolCall>()
                    : message.ToolCalls.Select(toolCall => new ToolCall
                    {
                        Id = toolCall.Id,
                        Name = toolCall.Name,
                        Input = toolCall.Input,
                        Description = toolCall.Description
                    }).ToList(),
                ToolCallId = message.ToolCallId,
                Name = message.Name,
                LocalMessageId = message.LocalMessageId,
                IsCompressedSummary = message.IsCompressedSummary,
                IsInternalObservation = message.IsInternalObservation,
                InternalObservationKind = message.InternalObservationKind,
                ToolName = message.ToolName,
                RawToolInput = message.RawToolInput,
                ToolSuccess = message.ToolSuccess
            };
        }
    }
}
