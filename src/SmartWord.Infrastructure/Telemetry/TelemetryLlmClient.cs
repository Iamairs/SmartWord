using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Core.Telemetry;

namespace SmartWord.Infrastructure.Telemetry
{
    /// <summary>
    /// 包装真实 LLM Client，记录调用耗时、token usage 和工具调用数量。
    /// </summary>
    public sealed class TelemetryLlmClient : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly IAgentTelemetrySink _telemetrySink;

        public TelemetryLlmClient(ILlmClient inner, IAgentTelemetrySink telemetrySink)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _telemetrySink = telemetrySink ?? NullAgentTelemetrySink.Instance;
        }

        public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var llmCallId = Guid.NewGuid().ToString("N");
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var completionBuilder = new System.Text.StringBuilder();
            try
            {
                await foreach (var chunk in _inner.ChatCompletionStreamAsync(messages, model, cancellationToken)
                    .ConfigureAwait(false))
                {
                    completionBuilder.Append(chunk);
                    yield return chunk;
                }

                stopwatch.Stop();
                await RecordLlmEventAsync(
                        "llm_call_completed",
                        llmCallId,
                        startedAt,
                        stopwatch.ElapsedMilliseconds,
                        model,
                        messages,
                        Array.Empty<ToolDefinition>(),
                        new LlmResponseMetadata
                        {
                            EstimatedPromptTokens = EstimateTokens(messages),
                            EstimatedCompletionTokens = EstimateTokens(completionBuilder.ToString()),
                            IsEstimatedUsage = true,
                            FinishReason = "stream_completed"
                        },
                        completionBuilder.ToString(),
                        0,
                        true,
                        string.Empty,
                        string.Empty,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
            }
        }

        public async Task<AgentMessage> ChatCompletionWithToolsAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition> tools,
            Action<string> onStreamChunk,
            CancellationToken cancellationToken)
        {
            var llmCallId = Guid.NewGuid().ToString("N");
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await _inner
                    .ChatCompletionWithToolsAsync(messages, model, tools, onStreamChunk, cancellationToken)
                    .ConfigureAwait(false);
                stopwatch.Stop();

                var metadata = response?.LlmMetadata ?? new LlmResponseMetadata
                {
                    EstimatedPromptTokens = EstimateTokens(messages),
                    EstimatedCompletionTokens = EstimateTokens(response == null ? string.Empty : response.Content),
                    IsEstimatedUsage = true,
                    FinishReason = response != null && response.ToolCalls != null && response.ToolCalls.Count > 0
                        ? "tool_calls"
                        : "stop"
                };

                if (!metadata.PromptTokens.HasValue && !metadata.TotalTokens.HasValue)
                {
                    metadata.EstimatedPromptTokens = metadata.EstimatedPromptTokens ?? EstimateTokens(messages);
                    metadata.EstimatedCompletionTokens = metadata.EstimatedCompletionTokens ?? EstimateTokens(response == null ? string.Empty : response.Content);
                    metadata.IsEstimatedUsage = true;
                }

                await RecordLlmEventAsync(
                        "llm_call_completed",
                        llmCallId,
                        startedAt,
                        stopwatch.ElapsedMilliseconds,
                        model,
                        messages,
                        tools,
                        metadata,
                        response == null ? string.Empty : response.Content,
                        response == null || response.ToolCalls == null ? 0 : response.ToolCalls.Count,
                        true,
                        string.Empty,
                        string.Empty,
                        cancellationToken)
                    .ConfigureAwait(false);

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                await RecordLlmEventAsync(
                        "llm_call_failed",
                        llmCallId,
                        startedAt,
                        stopwatch.ElapsedMilliseconds,
                        model,
                        messages,
                        tools,
                        new LlmResponseMetadata
                        {
                            EstimatedPromptTokens = EstimateTokens(messages),
                            IsEstimatedUsage = true
                        },
                        string.Empty,
                        0,
                        false,
                        ClassifyFailure(ex),
                        ex.Message,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
        }

        private async Task RecordLlmEventAsync(
            string eventType,
            string llmCallId,
            DateTimeOffset startedAt,
            long durationMs,
            string model,
            IReadOnlyList<AgentMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            LlmResponseMetadata metadata,
            string assistantContent,
            int toolCallCount,
            bool success,
            string failureType,
            string errorMessage,
            CancellationToken cancellationToken)
        {
            var e = CreateEvent(eventType);
            e.Model = model ?? string.Empty;
            e.Data["llmCallId"] = llmCallId;
            e.Data["model"] = model ?? string.Empty;
            e.Data["messageCount"] = messages == null ? 0 : messages.Count;
            e.Data["toolSchemaCount"] = tools == null ? 0 : tools.Count;
            e.Data["estimatedPromptTokens"] = metadata == null ? EstimateTokens(messages) : metadata.EstimatedPromptTokens;
            e.Data["estimatedCompletionTokens"] = metadata == null ? null : metadata.EstimatedCompletionTokens;
            e.Data["promptTokens"] = metadata == null ? null : metadata.PromptTokens;
            e.Data["completionTokens"] = metadata == null ? null : metadata.CompletionTokens;
            e.Data["totalTokens"] = metadata == null ? null : metadata.TotalTokens;
            e.Data["durationMs"] = durationMs;
            e.Data["finishReason"] = metadata == null ? string.Empty : metadata.FinishReason ?? string.Empty;
            e.Data["providerTraceId"] = metadata == null ? string.Empty : metadata.ProviderTraceId ?? string.Empty;
            e.Data["toolCallCount"] = toolCallCount;
            e.Data["assistantContent"] = Truncate(assistantContent, 4000);
            e.Data["success"] = success;
            e.Data["failureType"] = failureType ?? string.Empty;
            e.Data["errorMessage"] = errorMessage ?? string.Empty;
            e.Data["startedAtUtc"] = startedAt.ToString("O");
            e.Data["completedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
            await _telemetrySink.RecordAsync(e, cancellationToken).ConfigureAwait(false);
        }

        private static AgentTelemetryEvent CreateEvent(string eventType)
        {
            var e = AgentTelemetryEvent.Create(eventType);
            var context = AgentTelemetryScope.Current;
            if (context != null)
            {
                e.EvalRunId = context.EvalRunId;
                e.TaskRunId = context.TaskRunId;
                e.CaseId = context.CaseId;
                e.Level = context.Level;
                e.Variant = context.Variant;
            }

            return e;
        }

        private static int EstimateTokens(IReadOnlyList<AgentMessage> messages)
        {
            if (messages == null)
            {
                return 0;
            }

            return EstimateTokens(string.Join("\n", messages.Select(m =>
                (m == null ? string.Empty : m.Role + ":" + m.Content + " " + m.ReasoningContent))));
        }

        private static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
        }

        private static string ClassifyFailure(Exception ex)
        {
            if (ex is TimeoutException || ex is OperationCanceledException)
            {
                return "timeout";
            }

            if (ex is System.Net.Http.HttpRequestException)
            {
                return "http_error";
            }

            return "unknown_error";
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }
    }
}
