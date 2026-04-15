using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Application.Orchestration;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using Xunit;

namespace SmartWord.Application.Tests.Orchestration
{
    public class IntentRouterTests
    {
        [Fact]
        public async Task RouteAsync_LlmReturnsAsk_ReturnsAsk()
        {
            var router = new IntentRouter(
                new FakeLlmClient(chunks: new[] { "ask" }),
                "test-light-model");

            var result = await router.RouteAsync("解释一下这段内容", CreateDocumentContext(), CancellationToken.None);

            Assert.Equal(AgentMode.Ask, result);
        }

        [Fact]
        public async Task RouteAsync_LlmReturnsMixedCasePlan_ReturnsPlan()
        {
            var router = new IntentRouter(
                new FakeLlmClient(chunks: new[] { "PlAn" }),
                "test-light-model");

            var result = await router.RouteAsync("给我一个处理方案", CreateDocumentContext(), CancellationToken.None);

            Assert.Equal(AgentMode.Plan, result);
        }

        [Fact]
        public async Task RouteAsync_LlmReturnsMultipleKeywords_DefaultsToAgent()
        {
            var router = new IntentRouter(
                new FakeLlmClient(chunks: new[] { "ask or plan" }),
                "test-light-model");

            var result = await router.RouteAsync("请直接改文档", CreateDocumentContext(), CancellationToken.None);

            Assert.Equal(AgentMode.Agent, result);
        }

        [Fact]
        public async Task RouteAsync_LlmReturnsEmptyResponse_DefaultsToAgent()
        {
            var router = new IntentRouter(
                new FakeLlmClient(chunks: Array.Empty<string>()),
                "test-light-model");

            var result = await router.RouteAsync("请直接修改标题", CreateDocumentContext(), CancellationToken.None);

            Assert.Equal(AgentMode.Agent, result);
        }

        [Fact]
        public async Task RouteAsync_LlmThrows_FallsBackToKeywordRouting()
        {
            var router = new IntentRouter(
                new FakeLlmClient(streamException: new InvalidOperationException("boom")),
                "test-light-model");

            var result = await router.RouteAsync("请先规划一下步骤", CreateDocumentContext(), CancellationToken.None);

            Assert.Equal(AgentMode.Plan, result);
        }

        private static DocumentContext CreateDocumentContext()
        {
            return new DocumentContext
            {
                DocumentName = "demo.docx",
                DocumentPath = "C:/demo.docx",
                DocumentStatus = new DocumentStatus
                {
                    IsWritable = true
                }
            };
        }

        private sealed class FakeLlmClient : ILlmClient
        {
            private readonly IReadOnlyList<string> _chunks;
            private readonly Exception _streamException;

            public FakeLlmClient(IReadOnlyList<string> chunks = null, Exception streamException = null)
            {
                _chunks = chunks ?? Array.Empty<string>();
                _streamException = streamException;
            }

            public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                _ = messages;
                _ = model;
                cancellationToken.ThrowIfCancellationRequested();

                if (_streamException != null)
                {
                    throw _streamException;
                }

                foreach (var chunk in _chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                    yield return chunk;
                }
            }

            public Task<AgentMessage> ChatCompletionWithToolsAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                IReadOnlyList<ToolDefinition> tools,
                Action<string> onStreamChunk,
                CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }
        }
    }
}
