using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartWord.Application.Orchestration;
using SmartWord.Application.Tools;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using Xunit;

namespace SmartWord.Application.Tests.Orchestration
{
    public sealed class AgentOrchestrationComponentTests
    {
        [Fact]
        public void NormalizeToolCalls_缺少调用标识_补齐稳定前缀并规范空字段()
        {
            var calls = new List<ToolCall>
            {
                new ToolCall
                {
                    Id = string.Empty,
                    Name = null,
                    Input = null
                }
            };

            AgentOrchestratorUtilities.NormalizeToolCalls(calls, AgentMode.Plan, 1);

            Assert.StartsWith("autogen_plan_2_1_", calls[0].Id);
            Assert.Equal(string.Empty, calls[0].Name);
            Assert.Equal(string.Empty, calls[0].Input);
        }

        [Theory]
        [InlineData("总结这篇文档的内容", AgentMode.Ask, 0, true)]
        [InlineData("第15段的内容是什么", AgentMode.Ask, 0, true)]
        [InlineData("请规划这篇文档的章节重构", AgentMode.Plan, 0, true)]
        [InlineData("总结这篇文档的内容", AgentMode.Ask, 1, false)]
        [InlineData("你好", AgentMode.Ask, 0, false)]
        [InlineData("总结这篇文档的内容", AgentMode.Agent, 0, false)]
        public void RequiresFreshDocumentToolCall_不同输入与模式_返回预期结果(
            string userInput,
            AgentMode mode,
            int iteration,
            bool expected)
        {
            var actual = AgentOrchestratorUtilities.RequiresFreshDocumentToolCall(
                userInput,
                mode,
                iteration);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public async Task LlmTurnExecutor_要求首轮工具调用_使用严格工具选择扩展()
        {
            var client = new RecordingToolChoiceLlmClient();
            var executor = new LlmTurnExecutor(client);
            var updates = new List<LlmTurnUpdate>();

            await foreach (var update in executor.ExecuteAsync(
                new[] { new AgentMessage { Role = "user", Content = "总结这篇文档" } },
                new AgentRunOptions { Model = "test-model" },
                new[] { new ToolDefinition { Name = "probe_document" } },
                true,
                CancellationToken.None))
            {
                updates.Add(update);
            }

            Assert.True(client.LastRequireToolCall);
            Assert.Contains(updates, update => update.IsCompleted);
        }

        [Fact]
        public void DecorateToolOutput_读取段落结果_写入引用并构造引用列表()
        {
            var registry = new Dictionary<int, CitationEntry>();
            var paragraphToRef = new Dictionary<int, int>();
            var nextRef = 1;
            const string output = "{\"paragraphs\":[{\"index\":7,\"text\":\"测试段落\"}]}";

            var decorated = AgentEventFactory.DecorateToolOutput(
                "read_section",
                output,
                "C:\\docs\\sample.docx",
                registry,
                paragraphToRef,
                ref nextRef);
            var json = JObject.Parse(decorated);
            var citations = AgentEventFactory.BuildCitations("引用内容[1]", registry);

            Assert.Equal(1, json["paragraphs"]?[0]?["ref"]?.Value<int>());
            Assert.Equal(1, nextRef - 1);
            Assert.Single(citations);
            Assert.Equal(7, citations[0].ParagraphIndex);
        }

        [Fact]
        public void CreateMaxIterationsReachedEvent_Agent模式_保留暂停语义()
        {
            var result = AgentEventFactory.CreateMaxIterationsReachedEvent(
                AgentMode.Agent,
                100,
                string.Empty);

            Assert.Equal(AgentEventType.MaxIterationsReached, result.Type);
            Assert.Contains("暂停", result.Message);
            Assert.Contains("100", result.Message);
        }

        [Fact]
        public void PendingWriteStep_执行失败后再次失败_累加修复次数并保留最新调用()
        {
            var firstCall = new ToolCall { Id = "write_1", Name = "patch_range" };
            var firstResult = ToolCallResult.Error("patch_range", "第一次失败");
            var step = WriteOperationState.PendingWriteStep.CreateRepairRequired(
                firstCall,
                firstResult,
                "替换段落");

            var secondCall = new ToolCall { Id = "write_2", Name = "execute_script" };
            var secondResult = ToolCallResult.Error("execute_script", "第二次失败");
            var updated = step.RegisterWriteFailure(secondCall, secondResult, "执行修复脚本");

            Assert.Equal(2, updated.RepairAttempts);
            Assert.Equal("write_2", updated.ToolCallId);
            Assert.Equal(WriteOperationState.PendingWriteState.RepairRequired, updated.State);
            Assert.Contains("execute_script", updated.LastFailureMessage);
        }

        [Fact]
        public async Task PlanInterviewCoordinator_第三轮有效问题_先生成问题并返回强制收口消息()
        {
            var channel = new StubQuestionChannel("采用默认范围");
            var coordinator = new PlanInterviewCoordinator(channel);
            var toolCall = new ToolCall
            {
                Id = "question_3",
                Name = "ask_user_question",
                Input = "{\"question\":\"是否处理附录？\",\"options\":[\"处理\",\"跳过\"]}"
            };

            var request = coordinator.Prepare(toolCall, 2);
            var answer = await coordinator.WaitForAnswerAsync(toolCall.Id, CancellationToken.None);
            var limitMessage = coordinator.CreateRoundLimitMessage(answer);

            Assert.True(request.IsValid);
            Assert.True(request.ReachedLimit);
            Assert.Equal(3, request.InterviewRound);
            Assert.Equal(AgentEventType.QuestionAsked, request.QuestionEvent.Type);
            Assert.Equal(new[] { "处理", "跳过" }, request.QuestionEvent.QuestionOptions);
            Assert.Contains("立即输出执行计划", limitMessage.Content);
        }

        [Fact]
        public void PlanInterviewCoordinator_问题输入无法解析_返回无效请求()
        {
            var coordinator = new PlanInterviewCoordinator(null);
            var request = coordinator.Prepare(
                new ToolCall
                {
                    Id = "invalid_question",
                    Name = "ask_user_question",
                    Input = "{invalid"
                },
                0);

            Assert.False(request.IsValid);
            Assert.Equal(string.Empty, request.Question);
        }

        [Fact]
        public async Task ToolCallCoordinator_只读工具有效输入_返回允许且无需确认的准备结果()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubTool("read_sample", ToolPermission.ReadOnly));
            var coordinator = new ToolCallCoordinator(
                registry,
                new PermissionGuard(registry),
                null);

            var preparation = await coordinator.PrepareAsync(
                new ToolCall
                {
                    Id = "read_1",
                    Name = "read_sample",
                    Input = "{\"description\":\"读取示例区域\"}"
                },
                new AgentRunOptions { Mode = AgentMode.Ask },
                CancellationToken.None);

            Assert.NotNull(preparation.Tool);
            Assert.True(preparation.PermissionDecision.IsAllowed);
            Assert.False(preparation.RequiresConfirmation);
            Assert.Equal("读取示例区域", preparation.OperationDescription);
            Assert.Null(preparation.InputParseError);
        }

        [Fact]
        public async Task ToolCallCoordinator_工具输入不是Json_保留解析错误供主循环回填()
        {
            var registry = new ToolRegistry();
            registry.Register(new StubTool("read_sample", ToolPermission.ReadOnly));
            var coordinator = new ToolCallCoordinator(
                registry,
                new PermissionGuard(registry),
                null);

            var preparation = await coordinator.PrepareAsync(
                new ToolCall
                {
                    Id = "read_invalid",
                    Name = "read_sample",
                    Input = "{invalid"
                },
                new AgentRunOptions { Mode = AgentMode.Ask },
                CancellationToken.None);

            Assert.NotNull(preparation.InputParseError);
            Assert.False(preparation.InputParseError.Success);
            Assert.Contains("ERROR in read_sample", preparation.InputParseError.Output);
        }

        private sealed class RecordingToolChoiceLlmClient : ILlmClient, IToolChoiceLlmClient
        {
            public bool LastRequireToolCall { get; private set; }

            public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.CompletedTask;
                yield break;
            }

            public Task<AgentMessage> ChatCompletionWithToolsAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                IReadOnlyList<ToolDefinition> tools,
                Action<string> onStreamChunk,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new AgentMessage { Role = "assistant" });
            }

            public Task<AgentMessage> ChatCompletionWithToolsAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                IReadOnlyList<ToolDefinition> tools,
                bool requireToolCall,
                Action<string> onStreamChunk,
                CancellationToken cancellationToken)
            {
                LastRequireToolCall = requireToolCall;
                return Task.FromResult(new AgentMessage { Role = "assistant" });
            }
        }

        private sealed class StubQuestionChannel : IQuestionChannel
        {
            private readonly string _answer;

            internal StubQuestionChannel(string answer)
            {
                _answer = answer;
            }

            public bool IsAvailable => true;

            public Task<string> WaitForAnswerAsync(string questionId, CancellationToken cancellationToken)
            {
                return Task.FromResult(_answer);
            }
        }

        private sealed class StubTool : ITool
        {
            internal StubTool(string name, ToolPermission permission)
            {
                Name = name;
                RequiredPermission = permission;
            }

            public string Name { get; }

            public string Description => "测试工具";

            public ToolPermission RequiredPermission { get; }

            public bool IsVisibleToModel => true;

            public JsonElement InputSchema => default;

            public Task<ToolCallResult> ExecuteAsync(
                JsonElement input,
                IUndoScope undoScope,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(ToolCallResult.Ok("ok"));
            }
        }
    }
}
