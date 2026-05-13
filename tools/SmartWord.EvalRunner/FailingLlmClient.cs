using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.EvalRunner
{
    internal sealed class FailingLlmClient : ILlmClient
    {
        private readonly string _message;

        public FailingLlmClient(string message)
        {
            _message = message ?? "LLM Client 不可用。";
        }

        public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException(_message);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public Task<AgentMessage> ChatCompletionWithToolsAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition> tools,
            Action<string> onStreamChunk,
            CancellationToken cancellationToken)
        {
            return Task.FromException<AgentMessage>(new InvalidOperationException(_message));
        }
    }
}
