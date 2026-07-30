using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    internal sealed class ScriptedLlmClient : ILlmClient
    {
        private readonly Queue<AgentMessage> _responses;

        public ScriptedLlmClient(params AgentMessage[] responses)
        {
            _responses = new Queue<AgentMessage>(responses ?? Array.Empty<AgentMessage>());
        }

        public int CallCount { get; private set; }

        public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return string.Empty;
        }

        public Task<AgentMessage> ChatCompletionWithToolsAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition> tools,
            Action<string> onStreamChunk,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_responses.Count == 0
                ? new AgentMessage { Role = "assistant", Content = "完成。" }
                : _responses.Dequeue());
        }
    }
}
