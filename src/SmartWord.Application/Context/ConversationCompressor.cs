using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            if (messages == null || messages.Count == 0)
            {
                return Array.Empty<AgentMessage>();
            }

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
            var lastUserMessage = nonSystemMessages.LastOrDefault(IsUserMessage);
            var shouldReinsertUserMessage = lastUserMessage != null
                && !protocolSafeRecentMessages.Any(IsUserMessage);

            var result = new List<AgentMessage>(
                systemMessages.Count
                + protocolSafeRecentMessages.Count
                + (shouldReinsertUserMessage ? 2 : 1));
            result.AddRange(systemMessages);
            result.Add(new AgentMessage
            {
                Role = "system",
                Content = BuildSummaryContent(compactedMessages),
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

                if (IsToolMessage(message))
                {
                    // 压缩窗口不能以孤立 tool 结果开头；没有对应 assistant.tool_calls 时会被兼容接口拒绝。
                    continue;
                }

                if (IsAssistantMessage(message)
                    && message.ToolCalls != null
                    && message.ToolCalls.Count > 0)
                {
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

        private static string BuildSummaryContent(IReadOnlyList<AgentMessage> compactedMessages)
        {
            var userCount = compactedMessages.Count(message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
            var assistantCount = compactedMessages.Count(message =>
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
            var toolCount = compactedMessages.Count(message =>
                string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase));
            var lastUserMessage = compactedMessages.LastOrDefault(message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
            var lastAssistantMessage = compactedMessages.LastOrDefault(message =>
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                && !message.IsCompressedSummary);
            var lastToolMessage = compactedMessages.LastOrDefault(message =>
                string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase));

            var builder = new StringBuilder();
            builder.AppendLine("[历史摘要] 较早消息已压缩，请把以下内容视为已完成上下文。");
            builder.AppendLine("已压缩消息数：" + compactedMessages.Count);
            builder.AppendLine("用户消息数：" + userCount);
            builder.AppendLine("助手消息数：" + assistantCount);
            builder.AppendLine("工具消息数：" + toolCount);

            if (lastUserMessage != null)
            {
                builder.AppendLine("最近用户目标：" + SummarizeText(lastUserMessage.Content));
            }

            if (lastAssistantMessage != null)
            {
                builder.AppendLine("最近助手回复：" + SummarizeText(lastAssistantMessage.Content));
            }

            if (lastToolMessage != null)
            {
                builder.AppendLine(
                    "最近工具结果："
                    + (string.IsNullOrWhiteSpace(lastToolMessage.ToolName) ? "unknown" : lastToolMessage.ToolName)
                    + "，success="
                    + lastToolMessage.ToolSuccess);
            }

            return builder.ToString().Trim();
        }

        private static string SummarizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "empty";
            }

            var normalized = text
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            const int maxLength = 120;
            return normalized.Length <= maxLength
                ? normalized
                : normalized.Substring(0, maxLength) + "...";
        }

        private static bool IsUserMessage(AgentMessage message)
        {
            return message != null
                && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
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
