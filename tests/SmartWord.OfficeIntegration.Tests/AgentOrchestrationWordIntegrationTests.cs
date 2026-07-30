using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.Tests.Infrastructure;
using Xunit;

namespace SmartWord.OfficeIntegration.Tests
{
    [Collection(RealWordCollection.Name)]
    public sealed class AgentOrchestrationWordIntegrationTests
    {
        [WordIntegrationFact]
        public async Task Agent验证失败_当前写步骤回滚_产生失败事件()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var path = await session.CreateBasicFixtureAsync();
                await session.OpenDocumentAsync(path);
                var llm = new ScriptedLlmClient(
                    ToolResponse("write-fail", "execute_script", ExecuteScriptInput("未验证修改", false)),
                    FinalResponse());
                var events = await RunAgentAsync(session, llm);
                var text = await session.ReadActiveDocumentTextAsync();

                Assert.DoesNotContain("未验证修改", text);
                Assert.Contains(events, item => item.Type == AgentEventType.ChangeVerificationFailed);
                Assert.Contains(events, item => item.Type == AgentEventType.ChangeRepairRequired);
            });
        }

        [WordIntegrationFact]
        public async Task Agent多步骤写入_第二步失败_保留第一步已验证成果()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var path = await session.CreateBasicFixtureAsync();
                await session.OpenDocumentAsync(path);
                var llm = new ScriptedLlmClient(
                    ToolResponse("write-pass", "execute_script", ExecuteScriptInput("第一步已验证", true)),
                    ToolResponse("write-fail", "execute_script", ExecuteScriptInput("第二步未验证", false, 3)),
                    FinalResponse());
                var events = await RunAgentAsync(session, llm);
                var text = await session.ReadActiveDocumentTextAsync();

                Assert.Contains("第一步已验证", text);
                Assert.DoesNotContain("第二步未验证", text);
                Assert.Contains(events, item => item.Type == AgentEventType.ChangeApplied);
                Assert.Contains(events, item => item.Type == AgentEventType.ChangeVerificationFailed);
            });
        }

        [WordIntegrationFact]
        public async Task Agent等待写入确认时取消_文档保持不变_产生取消事件()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var path = await session.CreateBasicFixtureAsync();
                await session.OpenDocumentAsync(path);
                var originalText = await session.ReadActiveDocumentTextAsync();
                var llm = new ScriptedLlmClient(ToolResponse(
                    "patch-cancel",
                    "patch_range",
                    "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"不应写入\"}]}"));
                var orchestrator = ToolTestFactory.CreateOrchestrator(
                    session.WordWrapper,
                    llm,
                    new NeverConfirmChannel());
                var events = new List<AgentEvent>();
                using (var cancellation = new CancellationTokenSource(500))
                {
                    await foreach (var agentEvent in orchestrator.RunAsync(
                        "取消写入",
                        CreateOptions(AgentPermissionMode.ConfirmWrites),
                        cancellation.Token))
                    {
                        events.Add(agentEvent);
                    }
                }

                Assert.Equal(originalText, await session.ReadActiveDocumentTextAsync());
                Assert.Single(events.Where(item => item.Type == AgentEventType.Cancelled));
            });
        }

        [WordIntegrationFact]
        public async Task Agent只读文档_写入前拦截_不调用模型且文档不变()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var path = await session.CreateBasicFixtureAsync("read-only.docx");
                await session.OpenDocumentAsync(path, readOnly: true);
                var originalText = await session.ReadActiveDocumentTextAsync();
                var llm = new ScriptedLlmClient(ToolResponse(
                    "patch-read-only",
                    "patch_range",
                    "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"不应写入\"}]}"));

                var events = await RunAgentAsync(session, llm);

                Assert.Equal(0, llm.CallCount);
                Assert.Single(events.Where(item => item.Type == AgentEventType.DocumentNotWritable));
                Assert.Equal(originalText, await session.ReadActiveDocumentTextAsync());
            });
        }

        [WordIntegrationFact]
        public async Task Agent写入执行中取消_当前Undo步骤回滚_产生取消事件()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var path = await session.CreateBasicFixtureAsync("cancel-after-write.docx");
                await session.OpenDocumentAsync(path);
                var originalText = await session.ReadActiveDocumentTextAsync();
                var llm = new ScriptedLlmClient(ToolResponse(
                    "patch-cancel-after-write",
                    "patch_range",
                    "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"取消后应回滚\"}]}"));
                var undoScopeFactory = new TrackingUndoScopeFactory(session.WordWrapper);
                CancellableWriteTool cancellableTool = null;
                var orchestrator = ToolTestFactory.CreateOrchestrator(
                    session.WordWrapper,
                    llm,
                    configureRegistry: registry =>
                    {
                        cancellableTool = new CancellableWriteTool(registry.GetTool("patch_range"));
                        registry.Register(cancellableTool);
                    },
                    undoScopeFactory: undoScopeFactory);
                using (var cancellation = new CancellationTokenSource())
                {
                    var runTask = CollectAsync(orchestrator.RunAsync(
                        "写入后取消",
                        CreateOptions(AgentPermissionMode.FullAuto),
                        cancellation.Token));
                    var writeSignal = cancellableTool.WriteApplied;
                    var writeCompleted = await Task.WhenAny(writeSignal, Task.Delay(TimeSpan.FromSeconds(15)));
                    Assert.Same(writeSignal, writeCompleted);

                    cancellation.Cancel();
                    var runCompleted = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(15)));
                    Assert.Same(runTask, runCompleted);
                    var events = await runTask;

                    Assert.Single(events.Where(item => item.Type == AgentEventType.Cancelled));
                    Assert.Equal(1, undoScopeFactory.RollbackCount);
                    Assert.Equal(originalText, await session.ReadActiveDocumentTextAsync());
                }
            });
        }

        [WordIntegrationFact]
        public async Task Agent受保护文档_写入前拦截_不调用写工具()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var path = await session.CreateBasicFixtureAsync();
                await session.OpenDocumentAsync(path);
                await session.ProtectActiveDocumentAsync();
                var llm = new ScriptedLlmClient(ToolResponse(
                    "patch-protected",
                    "patch_range",
                    "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"不应写入\"}]}"));
                var events = await RunAgentAsync(session, llm);

                Assert.Contains(events, item => item.Type == AgentEventType.DocumentNotWritable);
                Assert.DoesNotContain("不应写入", await session.ReadActiveDocumentTextAsync());
            });
        }

        [WordIntegrationFact]
        public async Task Undo文档切换_回滚不误伤另一文档()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var firstPath = await session.CreateBasicFixtureAsync("first.docx");
                var secondPath = await session.CreateBasicFixtureAsync("second.docx");
                await session.OpenDocumentAsync(firstPath);
                await session.OpenDocumentAsync(secondPath);
                await session.ActivateDocumentAsync(firstPath);
                using (var undo = await session.WordWrapper.BeginWriteStepUndoAsync("文档切换测试"))
                {
                    var registry = ToolTestFactory.CreateRegistry(session.WordWrapper);
                    await OfficeToolIntegrationTests.ExecuteAsync(
                        registry.GetTool("patch_range"),
                        "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"第一文档修改\"}]}" );
                    await session.ActivateDocumentAsync(secondPath);
                    var secondBeforeRollback = await session.ReadActiveDocumentTextAsync();
                    undo.Rollback();
                    Assert.Equal(secondBeforeRollback, await session.ReadActiveDocumentTextAsync());
                }
            });
        }

        private static async Task<List<AgentEvent>> RunAgentAsync(
            WordTestSession session,
            ScriptedLlmClient llm)
        {
            var events = new List<AgentEvent>();
            var orchestrator = ToolTestFactory.CreateOrchestrator(session.WordWrapper, llm);
            await foreach (var agentEvent in orchestrator.RunAsync(
                "执行真实 Word 集成测试",
                CreateOptions(AgentPermissionMode.FullAuto),
                CancellationToken.None))
            {
                events.Add(agentEvent);
            }

            return events;
        }

        private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> stream)
        {
            var events = new List<AgentEvent>();
            await foreach (var agentEvent in stream)
            {
                events.Add(agentEvent);
            }

            return events;
        }

        private static AgentRunOptions CreateOptions(AgentPermissionMode permissionMode)
        {
            return new AgentRunOptions
            {
                Mode = AgentMode.Agent,
                Model = "scripted-test-model",
                PermissionMode = permissionMode,
                RequireConfirmationForScripts = permissionMode == AgentPermissionMode.ConfirmWrites,
                EnableToolCalling = true,
                MaxIterations = 10
            };
        }

        private static AgentMessage ToolResponse(string id, string name, string input)
        {
            return new AgentMessage
            {
                Role = "assistant",
                ToolCalls = new List<ToolCall>
                {
                    new ToolCall { Id = id, Name = name, Input = input, Description = "真实 Word 集成测试写入" }
                }
            };
        }

        private static AgentMessage FinalResponse()
        {
            return new AgentMessage { Role = "assistant", Content = "完成。" };
        }

        private static string ExecuteScriptInput(string text, bool verificationPassed, int paragraphNumber = 2)
        {
            var writeCode = "dynamic paragraph = ActiveDoc.Paragraphs[" + paragraphNumber + "]; dynamic range = paragraph.Range; range.Text = \"" + text + "\\r\"; return new { changed = true };";
            var verifyCode = "return new { all_passed = " + verificationPassed.ToString().ToLowerInvariant() + ", results = new [] { new { name = \"content\", passed = " + verificationPassed.ToString().ToLowerInvariant() + " } } };";
            return JsonSerializer.Serialize(new
            {
                description = "写入并验证真实 Word 段落",
                write_code = writeCode,
                verify_code = verifyCode
            });
        }

        private sealed class CancellableWriteTool : ITool
        {
            private readonly ITool _inner;
            private readonly TaskCompletionSource<bool> _writeApplied =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public CancellableWriteTool(ITool inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public string Name => _inner.Name;

            public string Description => _inner.Description;

            public ToolPermission RequiredPermission => _inner.RequiredPermission;

            public bool IsVisibleToModel => _inner.IsVisibleToModel;

            public JsonElement InputSchema => _inner.InputSchema;

            public Task WriteApplied => _writeApplied.Task;

            public async Task<ToolCallResult> ExecuteAsync(
                JsonElement input,
                IUndoScope undoScope,
                CancellationToken cancellationToken)
            {
                var result = await _inner.ExecuteAsync(input, undoScope, cancellationToken);
                if (!result.Success)
                {
                    return result;
                }

                _writeApplied.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return result;
            }
        }

        private sealed class TrackingUndoScopeFactory : IUndoScopeFactory
        {
            private readonly IUndoScopeFactory _inner;

            public TrackingUndoScopeFactory(IUndoScopeFactory inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public int RollbackCount { get; private set; }

            public async Task<IUndoScope> BeginWriteStepUndoAsync(
                string operationName,
                CancellationToken cancellationToken)
            {
                var innerScope = await _inner.BeginWriteStepUndoAsync(operationName, cancellationToken);
                return new TrackingUndoScope(innerScope, () => RollbackCount++);
            }
        }

        private sealed class TrackingUndoScope : IUndoScope
        {
            private readonly IUndoScope _inner;
            private readonly Action _onRollback;

            public TrackingUndoScope(IUndoScope inner, Action onRollback)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _onRollback = onRollback ?? throw new ArgumentNullException(nameof(onRollback));
            }

            public void Commit()
            {
                _inner.Commit();
            }

            public void Rollback()
            {
                _onRollback();
                _inner.Rollback();
            }

            public void Dispose()
            {
                _inner.Dispose();
            }
        }
    }
}
