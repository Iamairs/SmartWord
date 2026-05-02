using System;
using System.Collections.Generic;
using System.Linq;
using SmartWord.Core.Models;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 集中放置上下文压缩中复用的消息判断与复制逻辑。
    /// </summary>
    internal static class ConversationMessageUtilities
    {
        public static AgentMessage CloneMessage(AgentMessage message)
        {
            if (message == null)
            {
                return null;
            }

            return new AgentMessage
            {
                Role = message.Role,
                Content = message.Content,
                ReasoningContent = message.ReasoningContent,
                ToolCallId = message.ToolCallId,
                Name = message.Name,
                LocalMessageId = message.LocalMessageId,
                IsCompressedSummary = message.IsCompressedSummary,
                IsInternalObservation = message.IsInternalObservation,
                InternalObservationKind = message.InternalObservationKind,
                ToolName = message.ToolName,
                RawToolInput = message.RawToolInput,
                ToolSuccess = message.ToolSuccess,
                ToolCalls = message.ToolCalls == null
                    ? new List<ToolCall>()
                    : message.ToolCalls.Select(CloneToolCall).Where(item => item != null).ToList()
            };
        }

        public static ToolCall CloneToolCall(ToolCall toolCall)
        {
            if (toolCall == null)
            {
                return null;
            }

            return new ToolCall
            {
                Id = toolCall.Id,
                Name = toolCall.Name,
                Input = toolCall.Input,
                Description = toolCall.Description
            };
        }

        public static bool IsRole(AgentMessage message, string role)
        {
            return message != null
                && string.Equals(message.Role, role, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsUserMessage(AgentMessage message)
        {
            return IsRole(message, "user");
        }

        public static bool IsAssistantMessage(AgentMessage message)
        {
            return IsRole(message, "assistant");
        }

        public static bool IsToolMessage(AgentMessage message)
        {
            return IsRole(message, "tool");
        }

        public static bool IsWriteSafetyRelated(AgentMessage message)
        {
            if (message == null)
            {
                return false;
            }

            var toolName = message.ToolName ?? message.Name ?? string.Empty;
            if (IsWriteOrVerificationTool(toolName))
            {
                return true;
            }

            if (!message.ToolSuccess && IsToolMessage(message))
            {
                return true;
            }

            var content = message.Content ?? string.Empty;
            return content.IndexOf("自动验证", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("验证失败", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("已回滚", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("已回退", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("待修复", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("RepairRequired", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsWriteOrVerificationTool(string toolName)
        {
            return string.Equals(toolName, "patch_range", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "execute_script", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "verify_script", StringComparison.OrdinalIgnoreCase);
        }
    }
}
