using System.Collections.Generic;
using System.Linq;
using SmartWord.Application.Context;
using SmartWord.Core.Enums;
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
                CreateAssistantToolCall("call_3", "read_section"),
                CreateToolResult("call_3", "read_section", "{\"paragraphs\":[]}"),
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

        [Fact]
        public void Compress_AgentModeWithoutTodoBoard_SummarizesAsAllowedSimpleTask()
        {
            var compressor = new ConversationCompressor();
            var messages = CreateLongAgentConversation();

            var result = compressor.Compress(
                messages,
                new ConversationCompressionContext
                {
                    Mode = AgentMode.Agent,
                    CurrentUserGoal = "修改标题为宋体三号、正文缩进2字符",
                    CurrentTodoBoard = null
                });

            var summary = Assert.Single(result.Where(item => item.IsCompressedSummary));
            Assert.Contains("[Agent 执行状态]", summary.Content);
            Assert.Contains("Todo Board：当前未启用或为空", summary.Content);
            Assert.Contains("修改标题为宋体三号", summary.Content);
        }

        [Fact]
        public void Compress_AgentModeWithTodoBoard_IncludesTodoState()
        {
            var compressor = new ConversationCompressor();
            var messages = CreateLongAgentConversation();
            var board = new TodoBoard
            {
                ExecutionState = TodoBoardExecutionState.Running,
                Items = new List<TodoBoardItem>
                {
                    new TodoBoardItem { Id = "T1", Content = "处理标题", Status = TodoItemStatus.Completed, Order = 1 },
                    new TodoBoardItem { Id = "T2", Content = "处理正文缩进", Status = TodoItemStatus.InProgress, Order = 2 }
                }
            };

            var result = compressor.Compress(
                messages,
                new ConversationCompressionContext
                {
                    Mode = AgentMode.Agent,
                    CurrentTodoBoard = board
                });

            var summary = Assert.Single(result.Where(item => item.IsCompressedSummary));
            Assert.Contains("Todo Board 状态：Running", summary.Content);
            Assert.Contains("当前 Todo：T2", summary.Content);
            Assert.Contains("已完成 Todo：T1", summary.Content);
        }

        [Fact]
        public void Compress_AgentModeWithAutoVerifyObservation_IncludesVerificationMemory()
        {
            var compressor = new ConversationCompressor();
            var messages = CreateLongAgentConversation();
            messages.Add(new AgentMessage
            {
                Role = "user",
                Content = "[SmartWord 自动验证结果]\n当前写步骤“调整正文缩进”已自动验证通过且已提交。请继续执行后续 Todo，不要重复该步骤。",
                IsInternalObservation = true,
                InternalObservationKind = "auto_verify_result"
            });
            messages.Add(new AgentMessage { Role = "assistant", Content = "继续下一步。" });

            var result = compressor.Compress(
                messages,
                new ConversationCompressionContext
                {
                    Mode = AgentMode.Agent,
                    CurrentUserGoal = "修改标题为宋体三号、正文缩进2字符",
                    RecentInternalObservations = messages.Where(item => item.IsInternalObservation).ToList()
                });

            var summary = Assert.Single(result.Where(item => item.IsCompressedSummary));
            Assert.Contains("最近自动验证", summary.Content);
            Assert.Contains("已验证提交", summary.Content);
            Assert.Contains("不要重复已验证提交的写入", summary.Content);
        }

        [Fact]
        public void Compress_InternalObservationDoesNotReplaceRealUserGoal()
        {
            var compressor = new ConversationCompressor();
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "真实目标：修改正文缩进" },
                new AgentMessage { Role = "assistant", Content = "准备执行" },
                new AgentMessage { Role = "user", Content = "[SmartWord 自动验证结果] 验证失败", IsInternalObservation = true, InternalObservationKind = "auto_verify_result" },
                CreateAssistantToolCall("call_1", "read_script"),
                CreateToolResult("call_1", "read_script", "{\"ok\":true}"),
                CreateAssistantToolCall("call_2", "patch_range"),
                CreateToolResult("call_2", "patch_range", "{\"ok\":true}"),
                CreateAssistantToolCall("call_3", "read_section"),
                CreateToolResult("call_3", "read_section", "{\"paragraphs\":[]}")
            };

            var result = compressor.Compress(
                messages,
                new ConversationCompressionContext { Mode = AgentMode.Agent });

            Assert.Contains(result, item => item.Role == "user" && item.Content == "真实目标：修改正文缩进");
            Assert.DoesNotContain(result, item => item.Role == "user" && item.IsInternalObservation && !item.IsCompressedSummary);
        }

        [Fact]
        public void Compress_AskMode_DoesNotIncludeAgentWriteState()
        {
            var compressor = new ConversationCompressor();
            var result = compressor.Compress(
                CreateLongAgentConversation(),
                new ConversationCompressionContext { Mode = AgentMode.Ask });

            var summary = Assert.Single(result.Where(item => item.IsCompressedSummary));
            Assert.Contains("[Ask 状态]", summary.Content);
            Assert.DoesNotContain("[Agent 执行状态]", summary.Content);
            Assert.DoesNotContain("Todo Board", summary.Content);
        }

        [Fact]
        public void Compress_PlanMode_IncludesActivePlanWithoutTodoBoard()
        {
            var compressor = new ConversationCompressor();
            var result = compressor.Compress(
                CreateLongAgentConversation(),
                new ConversationCompressionContext
                {
                    Mode = AgentMode.Plan,
                    ActivePlan = new ExecutionPlan
                    {
                        TaskDescription = "统一论文格式",
                        TodoList = new List<TodoItem>
                        {
                            new TodoItem { Description = "统一标题" },
                            new TodoItem { Description = "统一正文缩进" }
                        }
                    }
                });

            var summary = Assert.Single(result.Where(item => item.IsCompressedSummary));
            Assert.Contains("[Plan 状态]", summary.Content);
            Assert.Contains("统一论文格式", summary.Content);
            Assert.Contains("统一标题", summary.Content);
            Assert.DoesNotContain("[Agent 执行状态]", summary.Content);
        }

        [Fact]
        public void Compress_DirectVerifyScriptHistory_IsNotPreservedAsModelToolChain()
        {
            var compressor = new ConversationCompressor();
            var messages = CreateLongAgentConversation();
            messages.Add(CreateAssistantToolCall("verify_1", "verify_script"));
            messages.Add(CreateToolResult("verify_1", "verify_script", "{\"all_passed\":true}"));

            var result = compressor.Compress(
                messages,
                new ConversationCompressionContext { Mode = AgentMode.Agent });

            Assert.DoesNotContain(
                result,
                item => item.ToolCalls != null
                    && item.ToolCalls.Any(toolCall => toolCall.Name == "verify_script"));
            Assert.DoesNotContain(result, item => item.Role == "tool" && item.ToolName == "verify_script");
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

        private static List<AgentMessage> CreateLongAgentConversation()
        {
            return new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "修改标题为宋体三号、正文缩进2字符" },
                new AgentMessage { Role = "assistant", Content = "先读取文档。" },
                CreateAssistantToolCall("call_1", "read_script"),
                CreateToolResult("call_1", "read_script", "{\"paragraph_count\":40}"),
                CreateAssistantToolCall("call_2", "patch_range"),
                CreateToolResult("call_2", "patch_range", "{\"success\":true,\"applied\":1,\"failed\":0,\"affected_paragraphs\":[1]}"),
                new AgentMessage { Role = "assistant", Content = "继续处理。" },
                CreateAssistantToolCall("call_3", "read_section"),
                CreateToolResult("call_3", "read_section", "{\"paragraphs\":[{\"para_index\":2}]}")
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
