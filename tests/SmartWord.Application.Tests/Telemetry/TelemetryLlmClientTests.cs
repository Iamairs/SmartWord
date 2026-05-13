using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Core.Telemetry;
using SmartWord.Infrastructure.Telemetry;
using Xunit;

namespace SmartWord.Application.Tests.Telemetry
{
    public sealed class TelemetryLlmClientTests
    {
        [Fact]
        public async Task ChatCompletionWithToolsAsync_ResponseHasUsage_RecordsRealTokens()
        {
            var sink = new CapturingSink();
            var inner = new FakeLlmClient(new AgentMessage
            {
                Role = "assistant",
                Content = "ok",
                LlmMetadata = new LlmResponseMetadata
                {
                    PromptTokens = 10,
                    CompletionTokens = 3,
                    TotalTokens = 13,
                    FinishReason = "stop"
                }
            });
            var client = new TelemetryLlmClient(inner, sink);

            await client.ChatCompletionWithToolsAsync(
                new[] { new AgentMessage { Role = "user", Content = "hello" } },
                "test-model",
                Array.Empty<ToolDefinition>(),
                null,
                CancellationToken.None);

            var e = Assert.Single(sink.Events);
            Assert.Equal("llm_call_completed", e.EventType);
            Assert.Equal(10, Assert.IsType<int>(e.Data["promptTokens"]));
            Assert.Equal(13, Assert.IsType<int>(e.Data["totalTokens"]));
        }

        [Fact]
        public async Task ChatCompletionWithToolsAsync_ResponseWithoutUsage_RecordsEstimatedTokens()
        {
            var sink = new CapturingSink();
            var inner = new FakeLlmClient(new AgentMessage { Role = "assistant", Content = "ok" });
            var client = new TelemetryLlmClient(inner, sink);

            await client.ChatCompletionWithToolsAsync(
                new[] { new AgentMessage { Role = "user", Content = "hello world" } },
                "test-model",
                Array.Empty<ToolDefinition>(),
                null,
                CancellationToken.None);

            var e = Assert.Single(sink.Events);
            Assert.True((int)e.Data["estimatedPromptTokens"] > 0);
            Assert.True((int)e.Data["estimatedCompletionTokens"] > 0);
        }

        private sealed class CapturingSink : IAgentTelemetrySink
        {
            public List<AgentTelemetryEvent> Events { get; } = new List<AgentTelemetryEvent>();

            public Task RecordAsync(AgentTelemetryEvent telemetryEvent, CancellationToken cancellationToken)
            {
                Events.Add(telemetryEvent);
                return Task.CompletedTask;
            }
        }

        private sealed class FakeLlmClient : ILlmClient
        {
            private readonly AgentMessage _response;

            public FakeLlmClient(AgentMessage response)
            {
                _response = response;
            }

            public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.Yield();
                yield return "ok";
            }

            public Task<AgentMessage> ChatCompletionWithToolsAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                IReadOnlyList<ToolDefinition> tools,
                Action<string> onStreamChunk,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(_response);
            }
        }
    }
}
