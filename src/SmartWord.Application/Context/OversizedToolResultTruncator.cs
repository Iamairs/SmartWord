using System;
using System.Collections.Generic;
using System.Linq;
using SmartWord.Core.Models;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 处理单条异常大的工具结果，避免一次工具输出占据过多上下文。
    /// </summary>
    public sealed class OversizedToolResultTruncator
    {
        private const double SingleToolResultContextShare = 0.20;
        private const int HeadChars = 3000;
        private const int TailChars = 3000;

        public IReadOnlyList<AgentMessage> Truncate(
            IReadOnlyList<AgentMessage> messages,
            ContextBudgetSnapshot budget)
        {
            if (messages == null || messages.Count == 0)
            {
                return Array.Empty<AgentMessage>();
            }

            var thresholdChars = ResolveThresholdChars(budget);
            var result = messages
                .Where(message => message != null)
                .Select(ConversationMessageUtilities.CloneMessage)
                .ToList();
            foreach (var message in result)
            {
                if (!ConversationMessageUtilities.IsToolMessage(message)
                    || ConversationMessageUtilities.IsWriteSafetyRelated(message)
                    || string.IsNullOrEmpty(message.Content)
                    || message.Content.Length <= thresholdChars)
                {
                    continue;
                }

                message.Content = TruncateToolResult(message, thresholdChars);
            }

            return result;
        }

        private static int ResolveThresholdChars(ContextBudgetSnapshot budget)
        {
            var contextWindow = budget == null || budget.ContextWindowTokens <= 0
                ? 256 * 1024
                : budget.ContextWindowTokens;
            return Math.Max(HeadChars + TailChars + 1000, (int)(contextWindow * SingleToolResultContextShare * 4));
        }

        private static string TruncateToolResult(AgentMessage message, int thresholdChars)
        {
            var content = message.Content ?? string.Empty;
            if (content.Length <= HeadChars + TailChars)
            {
                return content;
            }

            var head = content.Substring(0, HeadChars);
            var tail = content.Substring(content.Length - TailChars, TailChars);
            return head
                + Environment.NewLine
                + Environment.NewLine
                + "[SmartWord oversized tool result truncated: 单条工具结果超过上下文窗口比例限制，中间内容已省略。]"
                + Environment.NewLine
                + "tool=" + (string.IsNullOrWhiteSpace(message.ToolName) ? message.Name : message.ToolName)
                + Environment.NewLine
                + "tool_call_id=" + message.ToolCallId
                + Environment.NewLine
                + "original_chars=" + content.Length
                + Environment.NewLine
                + "threshold_chars=" + thresholdChars
                + Environment.NewLine
                + Environment.NewLine
                + tail;
        }
    }
}
