using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Application.Orchestration;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Tools;
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
        public async Task RunAsync_WriteToolRejectedByUser_EmitsSkippedAndDoesNotExecuteTool()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage("patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":0,\"text\":\"新的内容\"}]}"),
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
                tool,
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
            Assert.Equal(1, undoScopeFactory.LastScope.CommitCount);
            Assert.Equal(0, undoScopeFactory.LastScope.RollbackCount);
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
                tool,
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
        public async Task RunAsync_WriteToolSucceeded_EmitsChangeAppliedAndPassesUndoScope()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage("patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的标题\"}]}"),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成改写。"
                });
            var tool = new FakeTool(
                "patch_range",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 1 },
                    operationDescription: "已修改第 1 段。"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                tool,
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

            var changeApplied = Assert.Single(events.FindAll(item => item.Type == AgentEventType.ChangeApplied));
            Assert.Equal("patch_range", changeApplied.ToolName);
            Assert.Equal("已修改第 1 段。", changeApplied.OperationDescription);
            Assert.Equal(new[] { 1 }, changeApplied.AffectedParagraphs);
            Assert.NotNull(tool.LastUndoScope);
            Assert.Equal(1, undoScopeFactory.LastScope.CommitCount);
            Assert.Equal(0, undoScopeFactory.LastScope.RollbackCount);
        }

        private static AgentOrchestrator CreateOrchestrator(
            ILlmClient llmClient,
            IContextHydrator contextHydrator,
            ITool tool = null,
            IConfirmationChannel confirmationChannel = null,
            IUndoScopeFactory undoScopeFactory = null)
        {
            var registry = new ToolRegistry();
            if (tool != null)
            {
                registry.Register(tool);
            }

            return new AgentOrchestrator(
                llmClient,
                contextHydrator,
                new InMemoryConversationStore(),
                new SystemPromptBuilder(string.Empty),
                registry,
                new PermissionGuard(registry),
                confirmationChannel ?? new FakeConfirmationChannel(true, false),
                undoScopeFactory ?? new FakeUndoScopeFactory());
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

        private static AgentMessage CreateToolCallMessage(string toolName, string input)
        {
            return new AgentMessage
            {
                Role = "assistant",
                ToolCalls = new List<ToolCall>
                {
                    new ToolCall
                    {
                        Id = "tool-1",
                        Name = toolName,
                        Input = input
                    }
                }
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

        private sealed class FakeTool : ITool
        {
            private readonly JsonElement _schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
            private readonly ToolCallResult _result;

            public FakeTool(string name, ToolPermission permission, ToolCallResult result)
            {
                Name = name;
                RequiredPermission = permission;
                _result = result;
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
                return Task.FromResult(_result);
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
    }
}
