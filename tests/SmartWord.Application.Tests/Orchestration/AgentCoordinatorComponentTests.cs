using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartWord.Application.Orchestration;
using SmartWord.Application.Todo;
using SmartWord.Application.Tools;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Core.Telemetry;
using Xunit;

namespace SmartWord.Application.Tests.Orchestration
{
    public sealed class AgentCoordinatorComponentTests
    {
        [Fact]
        public async Task RunAuditRecorder_任务和工具记录_映射公共审计字段()
        {
            var historyStore = new RecordingTaskHistoryStore();
            var telemetrySink = new RecordingTelemetrySink();
            var recorder = new RunAuditRecorder(historyStore, telemetrySink);
            var options = new AgentRunOptions
            {
                Mode = AgentMode.Agent,
                Model = "test-model",
                PermissionMode = AgentPermissionMode.FullAuto
            };

            var run = await recorder.TryStartTaskRunAsync(
                @"C:\docs\sample.docx",
                "调整标题",
                options,
                CancellationToken.None);
            await recorder.TryRecordTaskToolAsync(
                run.Id,
                new ToolCall
                {
                    Id = "write-1",
                    Name = "patch_range",
                    Input = "{\"description\":\"调整标题\"}"
                },
                ToolCallResult.Ok("ok", operationDescription: "调整标题"),
                "调整标题",
                CancellationToken.None);

            Assert.Equal("run-1", run.Id);
            Assert.Equal(@"C:\docs\sample.docx", historyStore.StartRequest.DocumentPath);
            Assert.Equal("agent", historyStore.StartRequest.Mode);
            Assert.Equal("full_auto", historyStore.StartRequest.PermissionMode);
            Assert.Equal(0, historyStore.StartRequest.TotalSteps);
            Assert.Equal("write-1", historyStore.ToolRecord.ToolCallId);
            Assert.Equal("调整标题", historyStore.ToolRecord.OperationDescription);
        }

        [Fact]
        public async Task RunAuditRecorder_Telemetry接收端失败_不向主流程传播异常()
        {
            var recorder = new RunAuditRecorder(null, new ThrowingTelemetrySink());

            await recorder.RecordTaskTelemetryAsync(
                "task_started",
                new AgentRunOptions { Mode = AgentMode.Ask },
                new Dictionary<string, object> { ["status"] = "running" },
                CancellationToken.None);
        }

        [Fact]
        public async Task TodoRunCoordinator_Agent首次运行_发送就绪事件并标记运行开始()
        {
            var todoStore = new InMemoryTodoStore();
            var coordinator = new TodoRunCoordinator(new TodoManager(todoStore), null);
            var updates = new List<TodoRunStartupUpdate>();

            await foreach (var update in coordinator.StartRunAsync(
                @"C:\docs\sample.docx",
                new AgentRunOptions { Mode = AgentMode.Agent },
                CancellationToken.None))
            {
                updates.Add(update);
            }

            var started = Assert.Single(updates);
            Assert.True(started.RunStarted);
            Assert.False(started.ShouldStop);
            Assert.Equal(AgentEventType.TodoBoardReady, started.Event.Type);
            Assert.Equal(TodoBoardExecutionState.Running, started.Board.ExecutionState);
        }

        [Fact]
        public void WriteStepCoordinator_PatchRange替换文本_生成内部验证计划()
        {
            var coordinator = new WriteStepCoordinator(
                new ToolRegistry(),
                new RecordingConversationStore());
            var input = JObject.Parse(
                "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":2,\"text\":\"新标题\"}]}");

            var plan = coordinator.BuildAutoVerifyPlan("patch_range", input);

            Assert.True(plan.IsSupported);
            Assert.Equal("verify_script", plan.ToolName);
            Assert.Contains("text_equals", plan.InputJson);
            Assert.Contains("新标题", plan.InputJson);
        }

        [Fact]
        public void WriteStepCoordinator_自动验证失败_回滚Undo并进入待修复状态()
        {
            var coordinator = new WriteStepCoordinator(
                new ToolRegistry(),
                new RecordingConversationStore());
            var step = WriteOperationState.PendingWriteStep.CreateAwaitingVerification(
                new ToolCall { Id = "write-1", Name = "patch_range" },
                ToolCallResult.Ok("ok", operationDescription: "调整标题"),
                WriteOperationState.AutoVerifyPlan.Supported(
                    "verify_script",
                    "{\"code\":\"return true;\"}",
                    "验证标题"));
            var undoScope = new RecordingUndoScope();

            var transition = coordinator.ApplyVerificationOutcome(
                step,
                WriteOperationState.AutoVerifyOutcome.CreateFailed(
                    "标题验证失败",
                    "当前步骤待修复"),
                undoScope);

            Assert.False(transition.Passed);
            Assert.True(transition.UndoRolledBack);
            Assert.True(undoScope.RolledBack);
            Assert.False(undoScope.Committed);
            Assert.Equal(
                WriteOperationState.PendingWriteState.RepairRequired,
                transition.PendingWriteStep.State);
            Assert.Equal("标题验证失败", transition.PendingWriteStep.LastFailureMessage);
        }

        private sealed class RecordingTaskHistoryStore : ITaskHistoryStore
        {
            internal TaskRunStartRequest StartRequest { get; private set; }

            internal TaskToolRecord ToolRecord { get; private set; }

            public Task<TaskRunRecord> StartRunAsync(
                TaskRunStartRequest request,
                CancellationToken cancellationToken)
            {
                StartRequest = request;
                return Task.FromResult(new TaskRunRecord { Id = "run-1" });
            }

            public Task RecordToolAsync(
                string taskRunId,
                TaskToolRecord record,
                CancellationToken cancellationToken)
            {
                ToolRecord = record;
                return Task.CompletedTask;
            }

            public Task RecordChangeAsync(
                string taskRunId,
                TaskChangeRecord record,
                CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task CompleteRunAsync(
                string taskRunId,
                TaskRunCompletion completion,
                CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<TaskRunRecord>> GetRecentRunsAsync(
                string documentPath,
                int limit,
                CancellationToken cancellationToken)
            {
                return Task.FromResult<IReadOnlyList<TaskRunRecord>>(new List<TaskRunRecord>());
            }

            public Task<TaskRunDetail> GetRunDetailAsync(
                string taskRunId,
                CancellationToken cancellationToken)
            {
                return Task.FromResult<TaskRunDetail>(null);
            }
        }

        private sealed class RecordingTelemetrySink : IAgentTelemetrySink
        {
            public Task RecordAsync(
                AgentTelemetryEvent telemetryEvent,
                CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingTelemetrySink : IAgentTelemetrySink
        {
            public Task RecordAsync(
                AgentTelemetryEvent telemetryEvent,
                CancellationToken cancellationToken)
            {
                throw new System.InvalidOperationException("telemetry unavailable");
            }
        }

        private sealed class InMemoryTodoStore : ITodoStore
        {
            private TodoBoard _board;

            public Task<TodoBoard> GetBoardAsync(
                string documentPath,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(_board);
            }

            public Task SaveBoardAsync(
                TodoBoard board,
                CancellationToken cancellationToken)
            {
                _board = board;
                return Task.CompletedTask;
            }

            public Task DeleteBoardAsync(
                string documentPath,
                CancellationToken cancellationToken)
            {
                _board = null;
                return Task.CompletedTask;
            }

            public Task<bool> ExistsAsync(
                string documentPath,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(_board != null);
            }
        }

        private sealed class RecordingConversationStore : IConversationStore
        {
            public Task AppendUserMessageAsync(
                string documentPath,
                AgentMessage message,
                CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task AppendAssistantMessageAsync(
                string documentPath,
                AgentMessage message,
                CancellationToken cancellationToken)
            {
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
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<AgentMessage>> GetHistoryAsync(
                string documentPath,
                CancellationToken cancellationToken)
            {
                return Task.FromResult<IReadOnlyList<AgentMessage>>(new List<AgentMessage>());
            }

            public int EstimateTokenCount(IReadOnlyCollection<AgentMessage> messages)
            {
                return 0;
            }
        }

        private sealed class RecordingUndoScope : IUndoScope
        {
            internal bool Committed { get; private set; }

            internal bool RolledBack { get; private set; }

            public void Commit()
            {
                Committed = true;
            }

            public void Rollback()
            {
                RolledBack = true;
            }

            public void Dispose()
            {
            }
        }
    }
}
