using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Application.Context;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using Xunit;

namespace SmartWord.Application.Tests.Context
{
    public class ContextCompactionServiceTests
    {
        [Fact]
        public void Resolve_UsesContextWindowRatios_ReturnsExpectedLimits()
        {
            var policy = new ContextBudgetPolicy();

            var budget = policy.Resolve(new AgentRunOptions
            {
                ContextWindowTokens = 256 * 1024,
                ContextSoftLimitRatio = 0.65,
                ContextHardLimitRatio = 0.85,
                ContextEmergencyLimitRatio = 0.95,
                ContextTokenSafetyMargin = 1.2
            });

            Assert.Equal(256 * 1024, budget.ContextWindowTokens);
            Assert.Equal((int)((256 * 1024) * 0.65), budget.SoftLimitTokens);
            Assert.Equal((int)((256 * 1024) * 0.85), budget.HardLimitTokens);
            Assert.Equal((int)((256 * 1024) * 0.95), budget.EmergencyLimitTokens);
            Assert.Equal(120, policy.ApplySafetyMargin(100, budget));
        }

        [Fact]
        public void Prune_OldLargeToolResult_TrimsButPreservesFirstUserAndWriteSafetyMessages()
        {
            var pruner = new LightToolResultPruner();
            var oldToolContent = new string('a', 9000) + "TAIL";
            var writeToolContent = new string('b', 9000) + "WRITE_TAIL";
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "Skill 使用提示：请按当前技能规则执行。" },
                CreateAssistantToolCall("old-read", "read_section"),
                CreateToolResult("old-read", "read_section", oldToolContent),
                CreateAssistantToolCall("middle-read-1", "read_section"),
                CreateToolResult("middle-read-1", "read_section", "middle-1"),
                CreateAssistantToolCall("middle-read-2", "read_section"),
                CreateToolResult("middle-read-2", "read_section", "middle-2"),
                CreateAssistantToolCall("middle-read-3", "read_section"),
                CreateToolResult("middle-read-3", "read_section", "middle-3"),
                CreateAssistantToolCall("write-1", "patch_range"),
                CreateToolResult("write-1", "patch_range", writeToolContent),
                new AgentMessage { Role = "user", Content = "近期问题 1" },
                new AgentMessage { Role = "assistant", Content = "近期回答 1" },
                new AgentMessage { Role = "user", Content = "近期问题 2" },
                new AgentMessage { Role = "assistant", Content = "近期回答 2" },
                new AgentMessage { Role = "user", Content = "近期问题 3" },
                new AgentMessage { Role = "assistant", Content = "近期回答 3" }
            };

            var result = pruner.Prune(messages).ToList();

            Assert.Contains(result, item => item.Role == "user" && item.Content.StartsWith("Skill 使用提示"));
            var oldTool = result.Single(item => item.ToolCallId == "old-read");
            Assert.Contains("SmartWord tool result trimmed", oldTool.Content);
            Assert.Contains("TAIL", oldTool.Content);
            var writeTool = result.Single(item => item.ToolCallId == "write-1");
            Assert.Equal(writeToolContent, writeTool.Content);
        }

        [Fact]
        public async Task CompactIfNeededAsync_LlmCompactionTriggered_PreservesFirstUserAndAddsHardState()
        {
            var llmClient = new FakeLlmClient("[当前任务摘要]\n用户目标：整理文档。\n下一步：继续读取目标段落。");
            var service = new ContextCompactionService(llmClient, new ConversationCompressor());
            var messages = CreateLongHistory();

            var result = await service.CompactIfNeededAsync(
                messages,
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    Model = "fake-model",
                    ContextWindowTokens = 900,
                    ContextSoftLimitRatio = 0.20,
                    ContextHardLimitRatio = 0.95,
                    ContextEmergencyLimitRatio = 0.99
                },
                new ConversationCompressionContext
                {
                    Mode = AgentMode.Agent,
                    CurrentUserGoal = "整理文档",
                    PendingWriteStep = new PendingWriteStepSnapshot
                    {
                        ToolName = "patch_range",
                        OperationDescription = "修改第 3 段",
                        State = "RepairRequired",
                        RepairAttempts = 1,
                        LastFailureMessage = "验证失败，已回滚。"
                    }
                },
                CancellationToken.None);

            Assert.True(result.WasCompacted);
            Assert.False(result.ShouldStop);
            Assert.Contains(result.Messages, item => item.Role == "user" && item.Content.Contains("Skill 使用提示"));
            Assert.Contains(result.Messages, item => item.IsCompressedSummary && item.Content.Contains("[当前任务摘要]"));
            Assert.Contains(result.Messages, item => item.IsCompressedSummary && item.Content.Contains("[程序硬状态]"));
            Assert.Contains(result.Messages, item => item.Content.Contains("待修复"));
            Assert.DoesNotContain(result.Messages, item => item.Content.Contains("未验证写入"));
        }

        [Fact]
        public void Build_RepairRequiredState_DescribesRollbackInsteadOfUnverifiedWrite()
        {
            var builder = new ProgramHardStateBuilder();

            var hardState = builder.Build(new ConversationCompressionContext
            {
                Mode = AgentMode.Agent,
                PendingWriteStep = new PendingWriteStepSnapshot
                {
                    ToolName = "execute_script",
                    OperationDescription = "批量修改段落",
                    State = "RepairRequired",
                    RepairAttempts = 2,
                    LastFailureMessage = "验证失败，已回滚。"
                }
            });

            Assert.Contains("待修复", hardState);
            Assert.Contains("回滚", hardState);
            Assert.DoesNotContain("未验证写入", hardState);
        }

        private static List<AgentMessage> CreateLongHistory()
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "Skill 使用提示：请按当前技能规则执行。" }
            };
            for (var index = 0; index < 12; index++)
            {
                messages.Add(new AgentMessage { Role = "assistant", Content = "旧回答 " + index + " " + new string('x', 400) });
                messages.Add(new AgentMessage { Role = "user", Content = "旧问题 " + index + " " + new string('y', 400) });
            }

            for (var index = 0; index < 8; index++)
            {
                messages.Add(new AgentMessage { Role = index % 2 == 0 ? "user" : "assistant", Content = "近期短消息 " + index });
            }

            return messages;
        }

        private static AgentMessage CreateAssistantToolCall(string toolCallId, string toolName)
        {
            return new AgentMessage
            {
                Role = "assistant",
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
                Name = toolName,
                Content = content,
                RawToolInput = "{\"paragraphStart\":1,\"paragraphEnd\":3}",
                ToolSuccess = true
            };
        }

        private sealed class FakeLlmClient : ILlmClient
        {
            private readonly string _summary;

            public FakeLlmClient(string summary)
            {
                _summary = summary;
            }

            public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                _ = messages;
                _ = model;
                cancellationToken.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                yield break;
            }

            public Task<AgentMessage> ChatCompletionWithToolsAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                IReadOnlyList<ToolDefinition> tools,
                System.Action<string> onStreamChunk,
                CancellationToken cancellationToken)
            {
                _ = messages;
                _ = model;
                _ = tools;
                _ = onStreamChunk;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new AgentMessage
                {
                    Role = "assistant",
                    Content = _summary
                });
            }
        }
    }
}
