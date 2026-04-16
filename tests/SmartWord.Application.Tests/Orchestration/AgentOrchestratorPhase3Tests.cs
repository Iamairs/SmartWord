using System.Collections.Generic;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Application.Orchestration;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Tools;
using SmartWord.Application.Context;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.Persistence;
using Xunit;

namespace SmartWord.Application.Tests.Orchestration
{
    public class AgentOrchestratorPhase3Tests
    {
        [Fact]
        public async Task RunAsync_AgentModeDocumentNotWritable_StopsBeforeCallingModel()
        {
            var orchestrator = CreateOrchestrator(
                new FakeLlmClient(),
                new FakeContextHydrator(new DocumentContext
                {
                    DocumentPath = "doc1",
                    DocumentStatus = new DocumentStatus
                    {
                        IsWritable = false,
                        IsReadOnly = true
                    }
                }));

            var events = await CollectAsync(orchestrator.RunAsync(
                "请帮我改标题",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true
                },
                CancellationToken.None));

            Assert.Single(events);
            Assert.Equal(AgentEventType.DocumentNotWritable, events[0].Type);
        }

        [Fact]
        public async Task RunAsync_ModelCallFailsBeforeAnyWrite_DoesNotCreateUndoScope()
        {
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                new ThrowingLlmClient(new TimeoutException("等待 LLM 响应超时，超过 120 秒。")),
                CreateWritableHydrator(),
                undoScopeFactory: undoScopeFactory);

            await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                await CollectAsync(orchestrator.RunAsync(
                    "请修改标题格式",
                    new AgentRunOptions
                    {
                        Mode = AgentMode.Agent,
                        EnableToolCalling = true
                    },
                    CancellationToken.None));
            });

            Assert.Null(undoScopeFactory.LastScope);
        }

        [Fact]
        public async Task RunAsync_WriteToolRejectedByUser_EmitsSkippedAndDoesNotExecuteTool()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("tool-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":0,\"text\":\"新的内容\"}]}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成。"
                });
            var tool = new FakeTool("patch_range", ToolPermission.Write, ToolCallResult.Ok("{\"success\":true}"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { tool },
                new FakeConfirmationChannel(false, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请直接修改第一段",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ToolCallStarted && item.RequiresConfirmation);
            Assert.Contains(events, item => item.Type == AgentEventType.ToolCallSkipped);
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(0, tool.ExecutionCount);
            Assert.Null(undoScopeFactory.LastScope);
        }

        [Fact]
        public async Task RunAsync_ConsecutiveToolFailures_TriggersCircuitBreakerAndRollback()
        {
            var llmClient = new FakeLlmClient(new AgentMessage
            {
                Role = "assistant",
                ToolCalls = new List<ToolCall>
                {
                    new ToolCall { Id = "1", Name = "patch_range", Input = "{\"operations\":[]}" },
                    new ToolCall { Id = "2", Name = "patch_range", Input = "{\"operations\":[]}" },
                    new ToolCall { Id = "3", Name = "patch_range", Input = "{\"operations\":[]}" }
                }
            });
            var tool = new FakeTool("patch_range", ToolPermission.Write, ToolCallResult.Error("patch_range", "failed"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { tool },
                new FakeConfirmationChannel(true, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请执行多个写操作",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.Error && item.Message.Contains("连续失败"));
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(3, tool.ExecutionCount);
            Assert.Equal(0, undoScopeFactory.LastScope.CommitCount);
            Assert.Equal(1, undoScopeFactory.LastScope.RollbackCount);
        }

        [Fact]
        public async Task RunAsync_WriteToolSucceededAndVerifyPassed_EmitsExecutedAndVerifiedEvents()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的标题\"}]}"),
                    CreateToolCall("verify-1", "verify_change", "{\"checks\":[{\"type\":\"text_contains\",\"paragraph_index\":1,\"expected\":\"新的标题\"}]}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成改写。"
                });
            var writeTool = new FakeTool(
                "patch_range",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 1 },
                    operationDescription: "已修改第 1 段。"));
            var verifyTool = new FakeTool(
                "verify_change",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":true,\"results\":[]}",
                    new[] { 1 },
                    operationDescription: "已完成改动验证。"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, verifyTool },
                new FakeConfirmationChannel(true, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请修改第二段",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            var changeExecuted = Assert.Single(events.FindAll(item => item.Type == AgentEventType.ChangeExecuted));
            var changeApplied = Assert.Single(events.FindAll(item => item.Type == AgentEventType.ChangeApplied));
            Assert.Equal("patch_range", changeExecuted.ToolName);
            Assert.Equal("已修改第 1 段。", changeExecuted.OperationDescription);
            Assert.Equal(new[] { 1 }, changeExecuted.AffectedParagraphs);
            Assert.Equal("patch_range", changeApplied.ToolName);
            Assert.Equal("已修改第 1 段。", changeApplied.OperationDescription);
            Assert.Equal(new[] { 1 }, changeApplied.AffectedParagraphs);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeUnverified);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeVerificationFailed);
            Assert.NotNull(writeTool.LastUndoScope);
            Assert.Equal(1, undoScopeFactory.LastScope.CommitCount);
            Assert.Equal(0, undoScopeFactory.LastScope.RollbackCount);
        }

        [Fact]
        public async Task RunAsync_WriteToolSucceededWithoutVerify_AutoVerifyPassed_CompletesTask()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的标题\"}]}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成改写。"
                });
            var writeTool = new FakeTool(
                "patch_range",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 1 },
                    operationDescription: "已修改第 1 段。"));
            var verifyTool = new FakeTool(
                "verify_change",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":true,\"results\":[]}",
                    new[] { 1 },
                    operationDescription: "已完成改动验证。"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, verifyTool },
                new FakeConfirmationChannel(true, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请修改第二段",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeExecuted);
            var changeApplied = Assert.Single(events.FindAll(item => item.Type == AgentEventType.ChangeApplied));
            Assert.Equal("patch_range", changeApplied.ToolName);
            Assert.Equal("已修改第 1 段。", changeApplied.OperationDescription);
            Assert.Equal(new[] { 1 }, changeApplied.AffectedParagraphs);
            Assert.Contains("自动补验证", changeApplied.Message);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeUnverified);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeVerificationFailed);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallStarted
                && item.ToolCallId == "write-1__auto_verify"
                && item.ToolName == "verify_change");
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallCompleted
                && item.ToolCallId == "write-1__auto_verify"
                && item.ToolSuccess);
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(1, verifyTool.ExecutionCount);
            Assert.Equal(1, undoScopeFactory.LastScope.CommitCount);
            Assert.Equal(0, undoScopeFactory.LastScope.RollbackCount);
        }

        [Fact]
        public async Task RunAsync_WriteToolSucceededThenCalledNonVerifyTool_AutoVerifyPassedAndContinues()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的标题\"}]}"),
                    CreateToolCall("read-1", "read_section", "{\"heading\":\"第一章\"}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已结束。"
                });
            var writeTool = new FakeTool(
                "patch_range",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 1 },
                    operationDescription: "已修改第 1 段。"));
            var readTool = new FakeTool(
                "read_section",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok("{\"heading\":\"第一章\"}"));
            var verifyTool = new FakeTool(
                "verify_change",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":true,\"results\":[]}",
                    new[] { 1 },
                    operationDescription: "已完成改动验证。"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, readTool, verifyTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "请修改第二段后继续读取",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeExecuted);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ChangeApplied
                && item.Message.Contains("自动补验证"));
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeUnverified);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeVerificationFailed);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallStarted
                && item.ToolCallId == "write-1__auto_verify");
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(1, writeTool.ExecutionCount);
            Assert.Equal(1, verifyTool.ExecutionCount);
            Assert.Equal(1, readTool.ExecutionCount);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallCompleted
                && item.ToolCallId == "read-1"
                && item.ToolSuccess);
        }

        [Fact]
        public async Task RunAsync_VerifyChangeDidNotPass_EmitsChangeVerificationFailed()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的标题\"}]}"),
                    CreateToolCall("verify-1", "verify_change", "{\"checks\":[{\"type\":\"text_contains\",\"paragraph_index\":1,\"expected\":\"新的标题\"}]}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成改写。"
                });
            var writeTool = new FakeTool(
                "patch_range",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 1 },
                    operationDescription: "已修改第 1 段。"));
            var verifyTool = new FakeTool(
                "verify_change",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":false,\"results\":[{\"check_index\":0,\"passed\":false}]}",
                    new[] { 1 },
                    operationDescription: "已完成改动验证。"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, verifyTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "请修改第二段",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeExecuted);
            var verificationFailed = Assert.Single(events.FindAll(item => item.Type == AgentEventType.ChangeVerificationFailed));
            Assert.Equal("patch_range", verificationFailed.ToolName);
            Assert.Equal("已修改第 1 段。", verificationFailed.OperationDescription);
            Assert.Equal(new[] { 1 }, verificationFailed.AffectedParagraphs);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeApplied);
            Assert.Contains(events, item => item.Type == AgentEventType.Error && item.Message.Contains("未完成修复"));
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
        }

        [Fact]
        public async Task RunAsync_WriteToolFailedThenRepairSucceeded_CompletesAfterVerification()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "execute_script", "{\"description\":\"调整标题\",\"code\":\"bad\"}")),
                CreateToolCallMessage(
                    CreateToolCall("write-2", "execute_script", "{\"description\":\"调整标题\",\"code\":\"fixed\"}")),
                CreateToolCallMessage(
                    CreateToolCall("verify-1", "verify_change", "{\"checks\":[{\"type\":\"text_contains\",\"paragraph_index\":0,\"expected\":\"新标题\"}]}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成修复。"
                });
            var writeTool = new FakeTool(
                "execute_script",
                ToolPermission.Write,
                ToolCallResult.Error("execute_script", "脚本运行失败。"),
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 0 },
                    operationDescription: "已调整标题。"));
            var verifyTool = new FakeTool(
                "verify_change",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":true,\"results\":[]}",
                    new[] { 0 },
                    operationDescription: "已完成改动验证。"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, verifyTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "请修复标题格式",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeRepairRequired);
            Assert.Contains(events, item => item.Type == AgentEventType.ChangeExecuted);
            Assert.Contains(events, item => item.Type == AgentEventType.ChangeApplied);
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(2, writeTool.ExecutionCount);
        }

        [Fact]
        public async Task RunAsync_WriteToolFailedAndModelStopped_FailsTaskWithoutTaskCompleted()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "execute_script", "{\"description\":\"调整标题\",\"code\":\"bad\"}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "无法继续。"
                });
            var writeTool = new FakeTool(
                "execute_script",
                ToolPermission.Write,
                ToolCallResult.Error("execute_script", "脚本运行失败。"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "请调整标题格式",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item =>
                item.Type == AgentEventType.ChangeRepairRequired
                && item.ToolName == "execute_script");
            Assert.Contains(events, item => item.Type == AgentEventType.Error && item.Message.Contains("未完成修复"));
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
        }

        [Fact]
        public async Task RunAsync_ExecuteScriptSucceededWithoutExpectedChecks_AutoVerifyUnavailable_FailsTask()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "execute_script", "{\"description\":\"调整标题\",\"code\":\"ok\",\"affected_paragraphs\":[0]}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成脚本执行。"
                });
            var writeTool = new FakeTool(
                "execute_script",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 0 },
                    operationDescription: "已调整标题。"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool },
                new FakeConfirmationChannel(true, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请调整标题格式",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeExecuted);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ChangeVerificationFailed
                && item.Message.Contains("expected_checks"));
            Assert.Contains(events, item => item.Type == AgentEventType.Error && item.Message.Contains("无法自动补验证"));
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(0, undoScopeFactory.LastScope.CommitCount);
            Assert.Equal(1, undoScopeFactory.LastScope.RollbackCount);
        }

        [Fact]
        public async Task RunAsync_MoreThanTenToolCalls_TruncatesAtTen()
        {
            var toolCalls = Enumerable.Range(1, 11)
                .Select(index => CreateToolCall(index.ToString(), "read_section", "{\"heading\":\"第一章\"}"))
                .ToArray();
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(toolCalls),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已读取。"
                });
            var readTool = new FakeTool(
                "read_section",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok("{\"heading\":\"第一章\"}"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { readTool });

            var events = await CollectAsync(orchestrator.RunAsync(
                "请大量读取章节",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true
                },
                CancellationToken.None));

            Assert.Equal(10, readTool.ExecutionCount);
            Assert.Equal(10, events.Count(item => item.Type == AgentEventType.ToolCallStarted));
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
        }

        [Fact]
        public async Task RunAsync_ContextExceeded_CompressesHistoryAndContinues()
        {
            var llmClient = new FakeLlmClient(new AgentMessage
            {
                Role = "assistant",
                Content = "压缩后继续完成。"
            });
            var conversationStore = new FakeConversationStore(new[]
            {
                CreateHistoryMessage("user", "历史用户消息 1"),
                CreateHistoryMessage("assistant", "历史助手消息 1"),
                CreateHistoryMessage("user", "历史用户消息 2"),
                CreateHistoryMessage("assistant", "历史助手消息 2"),
                CreateHistoryMessage("user", "历史用户消息 3"),
                CreateHistoryMessage("assistant", "历史助手消息 3"),
                CreateHistoryMessage("user", "历史用户消息 4"),
                CreateHistoryMessage("assistant", "历史助手消息 4")
            });
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                conversationStore: conversationStore);

            var events = await CollectAsync(orchestrator.RunAsync(
                "继续处理当前任务",
                new AgentRunOptions
                {
                    Mode = AgentMode.Ask,
                    EnableToolCalling = false,
                    CompactionThreshold = 9000
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ContextCompacted);
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Contains(conversationStore.LastEstimatedMessages, item => item.IsCompressedSummary);
        }

        private static AgentOrchestrator CreateOrchestrator(
            ILlmClient llmClient,
            IContextHydrator contextHydrator,
            IEnumerable<ITool> tools = null,
            IConfirmationChannel confirmationChannel = null,
            IUndoScopeFactory undoScopeFactory = null,
            IConversationStore conversationStore = null)
        {
            var registry = new ToolRegistry();
            if (tools != null)
            {
                foreach (var tool in tools)
                {
                    if (tool != null)
                    {
                        registry.Register(tool);
                    }
                }
            }

            return new AgentOrchestrator(
                llmClient,
                contextHydrator,
                conversationStore ?? new InMemoryConversationStore(),
                new SystemPromptBuilder(string.Empty),
                registry,
                new PermissionGuard(registry),
                confirmationChannel ?? new FakeConfirmationChannel(true, false),
                undoScopeFactory ?? new FakeUndoScopeFactory(),
                new ConversationCompressor());
        }

        private static FakeContextHydrator CreateWritableHydrator()
        {
            return new FakeContextHydrator(new DocumentContext
            {
                DocumentPath = "doc1",
                DocumentStatus = new DocumentStatus
                {
                    IsWritable = true
                }
            });
        }

        private static AgentMessage CreateToolCallMessage(params ToolCall[] toolCalls)
        {
            return new AgentMessage
            {
                Role = "assistant",
                ToolCalls = toolCalls == null
                    ? new List<ToolCall>()
                    : new List<ToolCall>(toolCalls)
            };
        }

        private static ToolCall CreateToolCall(string id, string toolName, string input)
        {
            return new ToolCall
            {
                Id = id,
                Name = toolName,
                Input = input
            };
        }

        private static AgentMessage CreateHistoryMessage(string role, string content)
        {
            return new AgentMessage
            {
                Role = role,
                Content = content
            };
        }

        private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> stream)
        {
            var results = new List<AgentEvent>();
            await foreach (var item in stream)
            {
                results.Add(item);
            }

            return results;
        }

        private sealed class FakeLlmClient : ILlmClient
        {
            private readonly Queue<AgentMessage> _responses = new Queue<AgentMessage>();

            public FakeLlmClient(params AgentMessage[] responses)
            {
                foreach (var response in responses)
                {
                    _responses.Enqueue(response);
                }
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
                cancellationToken.ThrowIfCancellationRequested();

                var response = _responses.Count == 0
                    ? new AgentMessage
                    {
                        Role = "assistant",
                        Content = "done"
                    }
                    : _responses.Dequeue();
                if (!string.IsNullOrWhiteSpace(response.Content))
                {
                    onStreamChunk?.Invoke(response.Content);
                }

                return Task.FromResult(response);
            }
        }

        private sealed class FakeContextHydrator : IContextHydrator
        {
            private readonly DocumentContext _context;

            public FakeContextHydrator(DocumentContext context)
            {
                _context = context;
            }

            public Task<DocumentContext> HydrateAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_context);
            }
        }

        private sealed class ThrowingLlmClient : ILlmClient
        {
            private readonly Exception _exception;

            public ThrowingLlmClient(Exception exception)
            {
                _exception = exception;
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
                throw _exception;
#pragma warning disable CS0162
                yield break;
#pragma warning restore CS0162
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
                return Task.FromException<AgentMessage>(_exception);
            }
        }

        private sealed class FakeTool : ITool
        {
            private readonly JsonElement _schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
            private readonly Queue<ToolCallResult> _results = new Queue<ToolCallResult>();
            private ToolCallResult _lastResult;

            public FakeTool(string name, ToolPermission permission, params ToolCallResult[] results)
            {
                Name = name;
                RequiredPermission = permission;
                if (results != null)
                {
                    foreach (var result in results)
                    {
                        _results.Enqueue(result);
                        _lastResult = result;
                    }
                }
            }

            public string Name { get; }

            public string Description => Name;

            public ToolPermission RequiredPermission { get; }

            public JsonElement InputSchema => _schema;

            public int ExecutionCount { get; private set; }

            public IUndoScope LastUndoScope { get; private set; }

            public Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
            {
                _ = input;
                cancellationToken.ThrowIfCancellationRequested();
                ExecutionCount++;
                LastUndoScope = undoScope;
                if (_results.Count > 0)
                {
                    _lastResult = _results.Dequeue();
                }

                return Task.FromResult(_lastResult ?? ToolCallResult.Ok("{}"));
            }
        }

        private sealed class FakeConfirmationChannel : IConfirmationChannel
        {
            private readonly bool _confirmationResult;

            public FakeConfirmationChannel(bool confirmationResult, bool isAvailable)
            {
                _confirmationResult = confirmationResult;
                IsAvailable = isAvailable;
            }

            public bool IsAvailable { get; }

            public Task<bool> WaitForConfirmationAsync(string toolCallId, CancellationToken cancellationToken)
            {
                _ = toolCallId;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_confirmationResult);
            }
        }

        private sealed class FakeUndoScopeFactory : IUndoScopeFactory
        {
            public FakeUndoScope LastScope { get; private set; }

            public Task<IUndoScope> BeginTaskUndoAsync(string operationName, CancellationToken cancellationToken)
            {
                _ = operationName;
                cancellationToken.ThrowIfCancellationRequested();
                LastScope = new FakeUndoScope();
                return Task.FromResult<IUndoScope>(LastScope);
            }
        }

        private sealed class FakeUndoScope : IUndoScope
        {
            public int CommitCount { get; private set; }

            public int RollbackCount { get; private set; }

            public void Commit()
            {
                CommitCount++;
            }

            public void Rollback()
            {
                RollbackCount++;
            }

            public void Dispose()
            {
            }
        }

        private sealed class FakeConversationStore : IConversationStore
        {
            private readonly List<AgentMessage> _history;

            public FakeConversationStore(IEnumerable<AgentMessage> history)
            {
                _history = history == null
                    ? new List<AgentMessage>()
                    : new List<AgentMessage>(history);
                LastEstimatedMessages = new List<AgentMessage>();
            }

            public List<AgentMessage> LastEstimatedMessages { get; private set; }

            public Task AppendUserMessageAsync(string documentPath, AgentMessage message, CancellationToken cancellationToken)
            {
                _ = documentPath;
                cancellationToken.ThrowIfCancellationRequested();
                _history.Add(CloneMessage(message));
                return Task.CompletedTask;
            }

            public Task AppendAssistantMessageAsync(string documentPath, AgentMessage message, CancellationToken cancellationToken)
            {
                _ = documentPath;
                cancellationToken.ThrowIfCancellationRequested();
                _history.Add(CloneMessage(message));
                return Task.CompletedTask;
            }

            public Task AppendToolResultAsync(
                string documentPath,
                string toolCallId,
                string toolName,
                string rawInput,
                ToolCallResult result,
                CancellationToken cancellationToken)
            {
                _ = documentPath;
                _ = rawInput;
                cancellationToken.ThrowIfCancellationRequested();
                _history.Add(new AgentMessage
                {
                    Role = "tool",
                    ToolCallId = toolCallId ?? string.Empty,
                    ToolName = toolName ?? string.Empty,
                    Content = result == null ? string.Empty : result.Output,
                    ToolSuccess = result != null && result.Success
                });
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<AgentMessage>> GetHistoryAsync(string documentPath, CancellationToken cancellationToken)
            {
                _ = documentPath;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult((IReadOnlyList<AgentMessage>)_history.ConvertAll(CloneMessage));
            }

            public int EstimateTokenCount(IReadOnlyCollection<AgentMessage> messages)
            {
                LastEstimatedMessages = messages == null
                    ? new List<AgentMessage>()
                    : new List<AgentMessage>(messages.Select(CloneMessage));
                return (messages == null ? 0 : messages.Count) * 1000;
            }

            private static AgentMessage CloneMessage(AgentMessage message)
            {
                return new AgentMessage
                {
                    Role = message == null ? string.Empty : message.Role,
                    Content = message == null ? string.Empty : message.Content,
                    ReasoningContent = message == null ? string.Empty : message.ReasoningContent,
                    ToolCallId = message == null ? string.Empty : message.ToolCallId,
                    Name = message == null ? string.Empty : message.Name,
                    IsCompressedSummary = message != null && message.IsCompressedSummary,
                    ToolName = message == null ? string.Empty : message.ToolName,
                    RawToolInput = message == null ? string.Empty : message.RawToolInput,
                    ToolSuccess = message != null && message.ToolSuccess,
                    ToolCalls = message == null || message.ToolCalls == null
                        ? new List<ToolCall>()
                        : new List<ToolCall>(message.ToolCalls)
                };
            }
        }
    }
}
