using System;
using System.Collections.Generic;
using System.Linq;
using SmartWord.Core.Models;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 只对旧的大工具结果做轻量 head/tail 裁剪，不承担任务语义压缩职责。
    /// </summary>
    public sealed class LightToolResultPruner
    {
        private const int RecentUserTurnsToProtect = 3;
        private const int RecentAssistantChainsToProtect = 3;
        private const int MinToolResultChars = 8000;
        private const int HeadChars = 2000;
        private const int TailChars = 2000;

        public IReadOnlyList<AgentMessage> Prune(IReadOnlyList<AgentMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return Array.Empty<AgentMessage>();
            }

            var result = messages
                .Where(message => message != null)
                .Select(ConversationMessageUtilities.CloneMessage)
                .ToList();
            var protectedIndexes = BuildProtectedIndexes(result);

            for (var index = 0; index < result.Count; index++)
            {
                var message = result[index];
                if (protectedIndexes.Contains(index)
                    || !ConversationMessageUtilities.IsToolMessage(message)
                    || string.IsNullOrEmpty(message.Content)
                    || message.Content.Length < MinToolResultChars)
                {
                    continue;
                }

                message.Content = TrimToolResult(message);
            }

            return result;
        }

        private static HashSet<int> BuildProtectedIndexes(IReadOnlyList<AgentMessage> messages)
        {
            var protectedIndexes = new HashSet<int>();
            var firstUserIndex = FindFirstUserIndex(messages);
            if (firstUserIndex >= 0)
            {
                protectedIndexes.Add(firstUserIndex);
            }

            var recentUserStart = FindRecentUserTurnStart(messages, RecentUserTurnsToProtect);
            if (recentUserStart >= 0)
            {
                for (var index = recentUserStart; index < messages.Count; index++)
                {
                    protectedIndexes.Add(index);
                }
            }

            ProtectRecentAssistantChains(messages, protectedIndexes, RecentAssistantChainsToProtect);
            for (var index = 0; index < messages.Count; index++)
            {
                if (ConversationMessageUtilities.IsWriteSafetyRelated(messages[index]))
                {
                    protectedIndexes.Add(index);
                }
            }

            return protectedIndexes;
        }

        private static int FindFirstUserIndex(IReadOnlyList<AgentMessage> messages)
        {
            for (var index = 0; index < messages.Count; index++)
            {
                if (ConversationMessageUtilities.IsUserMessage(messages[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        private static int FindRecentUserTurnStart(IReadOnlyList<AgentMessage> messages, int turns)
        {
            var observed = 0;
            for (var index = messages.Count - 1; index >= 0; index--)
            {
                if (!ConversationMessageUtilities.IsUserMessage(messages[index]))
                {
                    continue;
                }

                observed++;
                if (observed >= turns)
                {
                    return index;
                }
            }

            return observed > 0 ? 0 : -1;
        }

        private static void ProtectRecentAssistantChains(
            IReadOnlyList<AgentMessage> messages,
            HashSet<int> protectedIndexes,
            int chains)
        {
            var observed = 0;
            for (var index = messages.Count - 1; index >= 0 && observed < chains; index--)
            {
                var message = messages[index];
                if (!ConversationMessageUtilities.IsAssistantMessage(message)
                    || message.ToolCalls == null
                    || message.ToolCalls.Count == 0)
                {
                    continue;
                }

                observed++;
                protectedIndexes.Add(index);
                var cursor = index + 1;
                while (cursor < messages.Count && ConversationMessageUtilities.IsToolMessage(messages[cursor]))
                {
                    protectedIndexes.Add(cursor);
                    cursor++;
                }
            }
        }

        private static string TrimToolResult(AgentMessage message)
        {
            var content = message.Content ?? string.Empty;
            if (content.Length <= HeadChars + TailChars)
            {
                return content;
            }

            var head = content.Substring(0, HeadChars);
            var tail = content.Substring(content.Length - TailChars, TailChars);
            var metadata = BuildTrimMetadata(message, content.Length);
            return head
                + Environment.NewLine
                + Environment.NewLine
                + metadata
                + Environment.NewLine
                + Environment.NewLine
                + tail;
        }

        private static string BuildTrimMetadata(AgentMessage message, int originalChars)
        {
            var parts = new List<string>
            {
                "[SmartWord tool result trimmed: 中间内容已省略，必要时请重新调用对应读取工具获取精确内容。]",
                "tool=" + (string.IsNullOrWhiteSpace(message.ToolName) ? message.Name : message.ToolName),
                "tool_call_id=" + message.ToolCallId,
                "success=" + message.ToolSuccess,
                "original_chars=" + originalChars
            };
            if (!string.IsNullOrWhiteSpace(message.RawToolInput))
            {
                parts.Add("args=" + Summarize(message.RawToolInput, 600));
            }

            return string.Join(Environment.NewLine, parts.Where(item => !string.IsNullOrWhiteSpace(item)));
        }

        private static string Summarize(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxChars) + "...";
        }
    }
}
