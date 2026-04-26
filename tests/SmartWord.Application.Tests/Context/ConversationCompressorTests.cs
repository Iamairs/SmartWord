using System.Collections.Generic;
using System.Linq;
using SmartWord.Application.Context;
using SmartWord.Core.Models;
using Xunit;

namespace SmartWord.Application.Tests.Context
{
    public class ConversationCompressorTests
    {
        [Fact]
        public void Compress_MessageCountNotExceeded_ReturnsOriginalShape()
        {
            var compressor = new ConversationCompressor();
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "user-1" },
                new AgentMessage { Role = "assistant", Content = "assistant-1" }
            };

            var result = compressor.Compress(messages);

            Assert.Equal(3, result.Count);
            Assert.DoesNotContain(result, item => item.IsCompressedSummary);
        }

        [Fact]
        public void Compress_MessageCountExceeded_PreservesSystemAndRecentMessages()
        {
            var compressor = new ConversationCompressor();
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "user-1" },
                new AgentMessage { Role = "assistant", Content = "assistant-1" },
                new AgentMessage { Role = "user", Content = "user-2" },
                new AgentMessage { Role = "assistant", Content = "assistant-2" },
                new AgentMessage { Role = "user", Content = "user-3" },
                new AgentMessage { Role = "assistant", Content = "assistant-3" },
                new AgentMessage { Role = "user", Content = "user-4" },
                new AgentMessage { Role = "assistant", Content = "assistant-4" }
            };

            var result = compressor.Compress(messages);

            Assert.Equal("system", result[0].Role);
            Assert.True(result[1].IsCompressedSummary);
            Assert.Contains("已压缩消息数：2", result[1].Content);
            Assert.Equal(
                new[] { "user-2", "assistant-2", "user-3", "assistant-3", "user-4", "assistant-4" },
                result.Skip(2).Select(item => item.Content).ToArray());
        }

        [Fact]
        public void Compress_RecentWindowStartsWithTool_ReinsertsUserAndKeepsToolCallPairs()
        {
            var compressor = new ConversationCompressor();
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "旧问题一" },
                new AgentMessage { Role = "assistant", Content = "旧回答一" },
                new AgentMessage { Role = "user", Content = "旧问题二" },
                new AgentMessage { Role = "assistant", Content = "旧回答二" },
                new AgentMessage { Role = "user", Content = "修改标题为宋体三号、正文缩进2字符" },
                CreateAssistantToolCall("call_1", "read_script"),
                CreateToolResult("call_1", "read_script", "{\"ok\":true}"),
                CreateAssistantToolCall("call_2", "patch_range"),
                CreateToolResult("call_2", "patch_range", "{\"ok\":true}"),
                CreateAssistantToolCall("call_3", "verify_script"),
                CreateToolResult("call_3", "verify_script", "{\"all_passed\":true}"),
                new AgentMessage { Role = "assistant", Content = "继续检查格式。" }
            };

            var result = compressor.Compress(messages);
            var nonSystemMessages = result
                .Where(message => message.Role != "system")
                .ToList();

            Assert.True(result.Count < messages.Count);
            Assert.Contains(
                nonSystemMessages,
                message => message.Role == "user"
                    && message.Content == "修改标题为宋体三号、正文缩进2字符");
            Assert.Equal("user", nonSystemMessages[0].Role);
            AssertToolMessagesHaveOwners(nonSystemMessages);
        }

        [Fact]
        public void Compress_IncompleteAssistantToolCall_DoesNotLeaveDanglingToolCall()
        {
            var compressor = new ConversationCompressor();
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "旧问题一" },
                new AgentMessage { Role = "assistant", Content = "旧回答一" },
                new AgentMessage { Role = "user", Content = "旧问题二" },
                new AgentMessage { Role = "assistant", Content = "旧回答二" },
                new AgentMessage { Role = "user", Content = "执行当前任务" },
                new AgentMessage { Role = "assistant", Content = "准备执行。" },
                CreateAssistantToolCall("call_dangling", "patch_range")
            };

            var result = compressor.Compress(messages);
            var nonSystemMessages = result
                .Where(message => message.Role != "system")
                .ToList();

            AssertToolMessagesHaveOwners(nonSystemMessages);
            Assert.DoesNotContain(
                nonSystemMessages,
                message => message.ToolCalls != null
                    && message.ToolCalls.Any(toolCall => toolCall.Id == "call_dangling"));
        }

        private static AgentMessage CreateAssistantToolCall(string toolCallId, string toolName)
        {
            return new AgentMessage
            {
                Role = "assistant",
                Content = string.Empty,
                ToolCalls = new List<ToolCall>
                {
                    new ToolCall
                    {
                        Id = toolCallId,
                        Name = toolName,
                        Input = "{}"
                    }
                }
            };
        }

        private static AgentMessage CreateToolResult(string toolCallId, string toolName, string content)
        {
            return new AgentMessage
            {
                Role = "tool",
                ToolCallId = toolCallId,
                ToolName = toolName,
                Content = content,
                ToolSuccess = true
            };
        }

        private static void AssertToolMessagesHaveOwners(IReadOnlyList<AgentMessage> messages)
        {
            var pendingToolCallIds = new HashSet<string>();
            foreach (var message in messages)
            {
                if (message.Role == "assistant")
                {
                    Assert.Empty(pendingToolCallIds);
                    foreach (var toolCall in message.ToolCalls ?? new List<ToolCall>())
                    {
                        pendingToolCallIds.Add(toolCall.Id);
                    }

                    continue;
                }

                if (message.Role == "tool")
                {
                    Assert.True(pendingToolCallIds.Remove(message.ToolCallId));
                    continue;
                }

                Assert.Empty(pendingToolCallIds);
            }

            Assert.Empty(pendingToolCallIds);
        }
    }
}
