using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 调用 LLM 生成统一的 Current Task Summary。
    /// </summary>
    public sealed class LlmHistoryCompactor
    {
        private readonly ILlmClient _llmClient;

        public LlmHistoryCompactor(ILlmClient llmClient)
        {
            _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        }

        public async Task<string> CompactAsync(
            IReadOnlyList<AgentMessage> messages,
            ConversationCompressionContext context,
            string programHardState,
            string model,
            CancellationToken cancellationToken)
        {
            var promptMessages = BuildPromptMessages(messages, context, programHardState);
            var response = await _llmClient
                .ChatCompletionWithToolsAsync(
                    promptMessages,
                    model ?? string.Empty,
                    Array.Empty<ToolDefinition>(),
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            var summary = response == null ? string.Empty : response.Content ?? string.Empty;
            return IsValidSummary(summary) ? summary.Trim() : string.Empty;
        }

        private static IReadOnlyList<AgentMessage> BuildPromptMessages(
            IReadOnlyList<AgentMessage> messages,
            ConversationCompressionContext context,
            string programHardState)
        {
            return new List<AgentMessage>
            {
                new AgentMessage
                {
                    Role = "system",
                    Content = BuildSystemPrompt()
                },
                new AgentMessage
                {
                    Role = "user",
                    Content = BuildUserPrompt(messages, context, programHardState)
                }
            };
        }

        private static string BuildSystemPrompt()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "你是 SmartWord 的上下文压缩器，不是在回答用户。",
                "你的任务是把较长的 Agent 历史压缩为一段 Current Task Summary，供后续 Word Agent 继续执行。",
                "只输出 Markdown 摘要，不输出解释、寒暄或工具调用。",
                "不要编造 Word 文档内容；精确内容需要时应让 Agent 重新读取。",
                "保留段落编号、tool_call_id、Todo id、标题、样式名等不透明标识，必须原样保留。",
                "用户最新指令优先；旧计划可以被新计划覆盖。",
                "如果失败写入已回滚，不要描述成仍存在于 Word 文档中。"
            });
        }

        private static string BuildUserPrompt(
            IReadOnlyList<AgentMessage> messages,
            ConversationCompressionContext context,
            string programHardState)
        {
            var builder = new StringBuilder();
            builder.AppendLine("请根据以下 SmartWord 会话历史生成统一的 [当前任务摘要]。");
            builder.AppendLine();
            builder.AppendLine("必须使用以下模板：");
            builder.AppendLine("[当前任务摘要]");
            builder.AppendLine("用户目标：");
            builder.AppendLine("近期用户问题 / 偏好：");
            builder.AppendLine("最新有效计划：");
            builder.AppendLine("近期进展 / 已完成修改：");
            builder.AppendLine("最近依赖的文档区域：");
            builder.AppendLine("最近写入、验证和回滚结果：");
            builder.AppendLine("关键约束：");
            builder.AppendLine("下一步：");
            builder.AppendLine();
            if (context != null && !string.IsNullOrWhiteSpace(context.CurrentUserGoal))
            {
                builder.AppendLine("最近真实用户目标：");
                builder.AppendLine(context.CurrentUserGoal.Trim());
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(programHardState))
            {
                builder.AppendLine("程序硬状态草稿：");
                builder.AppendLine(programHardState);
                builder.AppendLine();
            }

            builder.AppendLine("会话历史：");
            foreach (var message in messages ?? Array.Empty<AgentMessage>())
            {
                if (message == null)
                {
                    continue;
                }

                builder.AppendLine(FormatMessage(message));
            }

            return builder.ToString();
        }

        private static string FormatMessage(AgentMessage message)
        {
            var role = message.Role ?? string.Empty;
            var name = !string.IsNullOrWhiteSpace(message.ToolName)
                ? " tool=" + message.ToolName
                : string.Empty;
            var toolCallId = !string.IsNullOrWhiteSpace(message.ToolCallId)
                ? " tool_call_id=" + message.ToolCallId
                : string.Empty;
            var content = message.Content ?? string.Empty;
            if (content.Length > 6000)
            {
                content = content.Substring(0, 3000)
                    + Environment.NewLine
                    + "[中间内容省略]"
                    + Environment.NewLine
                    + content.Substring(content.Length - 3000, 3000);
            }

            return "## " + role + name + toolCallId + Environment.NewLine + content;
        }

        private static bool IsValidSummary(string summary)
        {
            if (string.IsNullOrWhiteSpace(summary))
            {
                return false;
            }

            return summary.IndexOf("用户目标", StringComparison.OrdinalIgnoreCase) >= 0
                && summary.IndexOf("下一步", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
