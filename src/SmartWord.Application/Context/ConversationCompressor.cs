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

            var keepRecentCount = Math.Min(DefaultRecentMessageCount, nonSystemMessages.Count);
            var compactedMessages = nonSystemMessages
                .Take(nonSystemMessages.Count - keepRecentCount)
                .ToList();
            if (compactedMessages.Count == 0)
            {
                return clonedMessages;
            }

            var recentMessages = nonSystemMessages
                .Skip(nonSystemMessages.Count - keepRecentCount)
                .Select(CloneMessage)
                .ToList();

            var result = new List<AgentMessage>(systemMessages.Count + recentMessages.Count + 1);
            result.AddRange(systemMessages);
            result.Add(new AgentMessage
            {
                Role = "system",
                Content = BuildSummaryContent(compactedMessages),
                IsCompressedSummary = true
            });
            result.AddRange(recentMessages);
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
                ToolName = message.ToolName,
                RawToolInput = message.RawToolInput,
                ToolSuccess = message.ToolSuccess
            };
        }
    }
}
