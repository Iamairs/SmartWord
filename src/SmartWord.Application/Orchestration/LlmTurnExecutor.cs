using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// 执行单轮 LLM 调用，并按原始顺序输出流式文本和最终消息。
    /// </summary>
    internal sealed class LlmTurnExecutor
    {
        private readonly ILlmClient _llmClient;

        internal LlmTurnExecutor(ILlmClient llmClient)
        {
            _llmClient = llmClient;
        }

        internal async IAsyncEnumerable<LlmTurnUpdate> ExecuteAsync(
            IReadOnlyList<AgentMessage> messages,
            AgentRunOptions options,
            IReadOnlyList<ToolDefinition> toolDefinitions,
            bool requireToolCall,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (toolDefinitions != null && toolDefinitions.Count > 0)
            {
                var chunks = new ConcurrentQueue<string>();
                using (var signal = new SemaphoreSlim(0))
                {
                    var streamChunkHandler = new Action<string>(chunk =>
                    {
                        chunks.Enqueue(chunk);
                        signal.Release();
                    });
                    var assistantTask = requireToolCall && _llmClient is IToolChoiceLlmClient toolChoiceClient
                        ? toolChoiceClient.ChatCompletionWithToolsAsync(
                            messages,
                            options.Model,
                            toolDefinitions,
                            true,
                            streamChunkHandler,
                            cancellationToken)
                        : _llmClient.ChatCompletionWithToolsAsync(
                            messages,
                            options.Model,
                            toolDefinitions,
                            streamChunkHandler,
                            cancellationToken);

                    while (!assistantTask.IsCompleted || !chunks.IsEmpty)
                    {
                        while (chunks.TryDequeue(out var chunk))
                        {
                            yield return LlmTurnUpdate.StreamChunk(chunk);
                        }

                        if (assistantTask.IsCompleted)
                        {
                            break;
                        }

                        var waitTask = signal.WaitAsync(cancellationToken);
                        var completedTask = await Task.WhenAny(assistantTask, waitTask).ConfigureAwait(false);
                        if (completedTask == waitTask)
                        {
                            await waitTask.ConfigureAwait(false);
                        }
                    }

                    while (chunks.TryDequeue(out var remainingChunk))
                    {
                        yield return LlmTurnUpdate.StreamChunk(remainingChunk);
                    }

                    if (assistantTask.IsCanceled)
                    {
                        yield return LlmTurnUpdate.Failed(new OperationCanceledException(cancellationToken));
                        yield break;
                    }

                    if (assistantTask.IsFaulted)
                    {
                        var exception = assistantTask.Exception?.GetBaseException();
                        yield return LlmTurnUpdate.Failed(
                            exception ?? new InvalidOperationException("当前 Agent 运行发生未预期异常。"));
                        yield break;
                    }

                    yield return LlmTurnUpdate.Completed(assistantTask.Result);
                }

                yield break;
            }

            var builder = new StringBuilder();
            await foreach (var chunk in _llmClient.ChatCompletionStreamAsync(
                messages,
                options.Model,
                cancellationToken))
            {
                if (string.IsNullOrEmpty(chunk))
                {
                    continue;
                }

                builder.Append(chunk);
                yield return LlmTurnUpdate.StreamChunk(chunk);
            }

            yield return LlmTurnUpdate.Completed(new AgentMessage
            {
                Role = "assistant",
                Content = builder.ToString()
            });
        }
    }

    /// <summary>
    /// 表示单轮模型调用产生的流式片段或最终消息。
    /// </summary>
    internal sealed class LlmTurnUpdate
    {
        internal string Chunk { get; private set; } = string.Empty;

        internal AgentMessage AssistantMessage { get; private set; }

        internal Exception Error { get; private set; }

        internal bool IsCompleted => AssistantMessage != null;

        internal bool IsFailed => Error != null;

        internal static LlmTurnUpdate StreamChunk(string chunk)
        {
            return new LlmTurnUpdate { Chunk = chunk ?? string.Empty };
        }

        internal static LlmTurnUpdate Completed(AgentMessage assistantMessage)
        {
            return new LlmTurnUpdate { AssistantMessage = assistantMessage };
        }

        internal static LlmTurnUpdate Failed(Exception error)
        {
            return new LlmTurnUpdate { Error = error };
        }
    }
}
