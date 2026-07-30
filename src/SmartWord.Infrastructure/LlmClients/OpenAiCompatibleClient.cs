using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Infrastructure.LlmClients
{
    /// <summary>
    /// 使用原生 HTTP + SSE 兼容 OpenAI Chat Completions 协议。
    /// </summary>
    public sealed class OpenAiCompatibleClient : ILlmClient, IDisposable
    {
        private const int SendRetryCount = 2;
        private static readonly TimeSpan SendRetryDelay = TimeSpan.FromMilliseconds(300);
        private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

        private sealed class ToolCallAccumulator
        {
            public string Id { get; set; } = string.Empty;

            public StringBuilder NameBuilder { get; } = new StringBuilder();

            public StringBuilder ArgumentsBuilder { get; } = new StringBuilder();
        }

        private readonly LlmClientOptions _options;

        public OpenAiCompatibleClient(LlmClientOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ValidateRequest(messages, model, out var endpoint, out var apiKey);
            var capability = _options.GetModelCapability(model);
            var requestJson = BuildRequestJson(model, messages, null, capability);
            var responseHeadersTimeout = ResolveStreamPhaseTimeout(_options.TimeoutSeconds);
            var firstLineTimeout = ResolveStreamPhaseTimeout(_options.TimeoutSeconds);
            var nextLineTimeout = ResolveStreamPhaseTimeout(_options.TimeoutSeconds);

            using (var responseHeadersTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(endpoint)))
            {
                responseHeadersTimeoutCts.CancelAfter(responseHeadersTimeout);

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Content = BuildRequestContent(requestJson);

                Log.Information(
                    "开始调用 LLM 流式接口。Endpoint={Endpoint}, Model={Model}, SupportsToolCalling={SupportsToolCalling}, RequiresReasoningContentReplay={RequiresReasoningContentReplay}, MessageCount={MessageCount}, MessageSummary={MessageSummary}, ResponseHeadersTimeoutSeconds={ResponseHeadersTimeoutSeconds}, FirstLineTimeoutSeconds={FirstLineTimeoutSeconds}, NextLineTimeoutSeconds={NextLineTimeoutSeconds}, RequestBodyLength={RequestBodyLength}",
                    request.RequestUri,
                    model,
                    capability == null ? false : capability.SupportsToolCalling,
                    capability == null ? false : capability.RequiresReasoningContentReplay,
                    messages == null ? 0 : messages.Count,
                    BuildMessageSummary(messages),
                    responseHeadersTimeout.TotalSeconds,
                    firstLineTimeout.TotalSeconds,
                    nextLineTimeout.TotalSeconds,
                    GetTextLength(requestJson));

                HttpResponseMessage response = null;
                try
                {
                    response = await SendStreamingRequestWithRetryAsync(
                            request.RequestUri,
                            apiKey,
                            requestJson,
                            responseHeadersTimeoutCts.Token,
                            "流式接口",
                            model)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                    when (!cancellationToken.IsCancellationRequested && responseHeadersTimeoutCts.IsCancellationRequested)
                {
                    Log.Warning(
                        ex,
                        "等待 LLM 流式接口响应头超时。Endpoint={Endpoint}, Model={Model}, TimeoutSeconds={TimeoutSeconds}, RequestBodyLength={RequestBodyLength}",
                        request.RequestUri,
                        model,
                        responseHeadersTimeout.TotalSeconds,
                        GetTextLength(requestJson));
                    throw new TimeoutException(
                        $"等待 LLM 响应超时，超过 {responseHeadersTimeout.TotalSeconds} 秒。",
                        ex);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Log.Error(
                            "LLM 流式接口返回错误。Endpoint={Endpoint}, Model={Model}, StatusCode={StatusCode}, ReasonPhrase={ReasonPhrase}, TraceId={TraceId}, ResponseHeaders={ResponseHeaders}, ResponseBodySummary={ResponseBodySummary}, RequestBodyLength={RequestBodyLength}",
                            request.RequestUri,
                            model,
                            (int)response.StatusCode,
                            response.ReasonPhrase,
                            GetTraceId(response),
                            FormatHeaders(response.Headers, response.Content == null ? null : response.Content.Headers),
                            SummarizeBody(errorBody),
                            GetTextLength(requestJson));
                        throw new HttpRequestException(
                            $"LLM 请求失败：{(int)response.StatusCode} {response.ReasonPhrase} {errorBody}，TraceId={GetTraceId(response)}");
                    }

                    Log.Information(
                        "LLM 流式接口已建立连接。Endpoint={Endpoint}, Model={Model}, StatusCode={StatusCode}, TraceId={TraceId}, ResponseHeaders={ResponseHeaders}",
                        request.RequestUri,
                        model,
                        (int)response.StatusCode,
                        GetTraceId(response),
                        FormatHeaders(response.Headers, response.Content == null ? null : response.Content.Headers));

                    using (var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                    {
                        var hasSeenFirstDataLine = false;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var line = await ReadLineWithTimeoutAsync(
                                    reader,
                                    hasSeenFirstDataLine ? nextLineTimeout : firstLineTimeout,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            if (line == null)
                            {
                                yield break;
                            }

                            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            hasSeenFirstDataLine = true;
                            var payload = line.Substring("data:".Length).Trim();
                            if (string.IsNullOrWhiteSpace(payload))
                            {
                                continue;
                            }

                            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                            {
                                yield break;
                            }

                            JObject jsonPayload;
                            try
                            {
                                jsonPayload = JObject.Parse(payload);
                            }
                            catch (JsonReaderException)
                            {
                                continue;
                            }

                            var content = jsonPayload["choices"]?[0]?["delta"]?["content"]?.Value<string>();
                            if (!string.IsNullOrEmpty(content))
                            {
                                yield return content;
                            }

                            var finishReason = jsonPayload["choices"]?[0]?["finish_reason"]?.Value<string>();
                            if (!string.IsNullOrWhiteSpace(finishReason))
                            {
                                yield break;
                            }
                        }
                    }
                }
            }
        }

        public async Task<AgentMessage> ChatCompletionWithToolsAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition> tools,
            Action<string> onStreamChunk,
            CancellationToken cancellationToken)
        {
            if (tools == null)
            {
                throw new ArgumentNullException(nameof(tools));
            }

            ValidateRequest(messages, model, out var endpoint, out var apiKey);
            var capability = _options.GetModelCapability(model);
            var requestJson = BuildRequestJson(model, messages, tools, capability);
            var responseHeadersTimeout = ResolveStreamPhaseTimeout(_options.TimeoutSeconds);
            var firstLineTimeout = ResolveStreamPhaseTimeout(_options.TimeoutSeconds);
            var nextLineTimeout = ResolveStreamPhaseTimeout(_options.TimeoutSeconds);

            var textBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var toolCallAccumulators = new Dictionary<int, ToolCallAccumulator>();
            var responseMetadata = new LlmResponseMetadata();

            using (var responseHeadersTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(endpoint)))
            {
                responseHeadersTimeoutCts.CancelAfter(responseHeadersTimeout);

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Content = BuildRequestContent(requestJson);

                Log.Information(
                    "开始调用 LLM 工具接口。Endpoint={Endpoint}, Model={Model}, SupportsToolCalling={SupportsToolCalling}, RequiresReasoningContentReplay={RequiresReasoningContentReplay}, ToolCount={ToolCount}, MessageCount={MessageCount}, MessageSummary={MessageSummary}, ToolSummary={ToolSummary}, ResponseHeadersTimeoutSeconds={ResponseHeadersTimeoutSeconds}, FirstLineTimeoutSeconds={FirstLineTimeoutSeconds}, NextLineTimeoutSeconds={NextLineTimeoutSeconds}, RequestBodyLength={RequestBodyLength}",
                    request.RequestUri,
                    model,
                    capability == null ? false : capability.SupportsToolCalling,
                    capability == null ? false : capability.RequiresReasoningContentReplay,
                    tools.Count,
                    messages == null ? 0 : messages.Count,
                    BuildMessageSummary(messages),
                    BuildToolSummary(tools),
                    responseHeadersTimeout.TotalSeconds,
                    firstLineTimeout.TotalSeconds,
                    nextLineTimeout.TotalSeconds,
                    GetTextLength(requestJson));

                HttpResponseMessage response = null;
                try
                {
                    response = await SendStreamingRequestWithRetryAsync(
                            request.RequestUri,
                            apiKey,
                            requestJson,
                            responseHeadersTimeoutCts.Token,
                            "工具接口",
                            model)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                    when (!cancellationToken.IsCancellationRequested && responseHeadersTimeoutCts.IsCancellationRequested)
                {
                    Log.Warning(
                        ex,
                        "等待 LLM 工具接口响应头超时。Endpoint={Endpoint}, Model={Model}, TimeoutSeconds={TimeoutSeconds}, ToolCount={ToolCount}, RequestBodyLength={RequestBodyLength}",
                        request.RequestUri,
                        model,
                        responseHeadersTimeout.TotalSeconds,
                        tools == null ? 0 : tools.Count,
                        GetTextLength(requestJson));
                    throw new TimeoutException(
                        $"等待 LLM 响应超时，超过 {responseHeadersTimeout.TotalSeconds} 秒。",
                        ex);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Log.Error(
                            "LLM 工具接口返回错误。Endpoint={Endpoint}, Model={Model}, StatusCode={StatusCode}, ReasonPhrase={ReasonPhrase}, TraceId={TraceId}, ResponseHeaders={ResponseHeaders}, ResponseBodySummary={ResponseBodySummary}, RequestBodyLength={RequestBodyLength}",
                            request.RequestUri,
                            model,
                            (int)response.StatusCode,
                            response.ReasonPhrase,
                            GetTraceId(response),
                            FormatHeaders(response.Headers, response.Content == null ? null : response.Content.Headers),
                            SummarizeBody(errorBody),
                            GetTextLength(requestJson));
                        throw new HttpRequestException(
                            $"LLM 请求失败：{(int)response.StatusCode} {response.ReasonPhrase} {errorBody}，TraceId={GetTraceId(response)}");
                    }

                    Log.Information(
                        "LLM 工具接口已建立连接。Endpoint={Endpoint}, Model={Model}, StatusCode={StatusCode}, TraceId={TraceId}, ResponseHeaders={ResponseHeaders}",
                        request.RequestUri,
                        model,
                        (int)response.StatusCode,
                        GetTraceId(response),
                        FormatHeaders(response.Headers, response.Content == null ? null : response.Content.Headers));

                    using (var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                    {
                        var hasSeenFirstDataLine = false;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var line = await ReadLineWithTimeoutAsync(
                                    reader,
                                    hasSeenFirstDataLine ? nextLineTimeout : firstLineTimeout,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            if (line == null)
                            {
                                break;
                            }

                            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            hasSeenFirstDataLine = true;
                            var payload = line.Substring("data:".Length).Trim();
                            if (string.IsNullOrWhiteSpace(payload))
                            {
                                continue;
                            }

                            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }

                            JObject jsonPayload;
                            try
                            {
                                jsonPayload = JObject.Parse(payload);
                            }
                            catch (JsonReaderException)
                            {
                                continue;
                            }

                            var choice = jsonPayload["choices"]?[0];
                            var delta = choice?["delta"];

                            var content = delta?["content"]?.Value<string>();
                            if (!string.IsNullOrEmpty(content))
                            {
                                textBuilder.Append(content);
                                onStreamChunk?.Invoke(content);
                            }

                            var reasoningContent = delta?["reasoning_content"]?.Value<string>();
                            if (!string.IsNullOrEmpty(reasoningContent))
                            {
                                reasoningBuilder.Append(reasoningContent);
                            }

                            var toolCalls = delta?["tool_calls"] as JArray;
                            if (toolCalls != null)
                            {
                                foreach (var toolCallToken in toolCalls)
                                {
                                    var index = toolCallToken?["index"]?.Value<int?>() ?? 0;
                                    if (!toolCallAccumulators.TryGetValue(index, out var accumulator))
                                    {
                                        accumulator = new ToolCallAccumulator();
                                        toolCallAccumulators[index] = accumulator;
                                    }

                                    var id = toolCallToken?["id"]?.Value<string>();
                                    if (!string.IsNullOrWhiteSpace(id))
                                    {
                                        accumulator.Id = id;
                                    }

                                    var function = toolCallToken?["function"];
                                    var namePart = function?["name"]?.Value<string>();
                                    if (!string.IsNullOrEmpty(namePart))
                                    {
                                        accumulator.NameBuilder.Append(namePart);
                                    }

                                    var argumentsPart = function?["arguments"]?.Value<string>();
                                    if (!string.IsNullOrEmpty(argumentsPart))
                                    {
                                        accumulator.ArgumentsBuilder.Append(argumentsPart);
                                    }
                                }
                            }

                            var finishReason = choice?["finish_reason"]?.Value<string>();
                            if (!string.IsNullOrWhiteSpace(finishReason))
                            {
                                responseMetadata.FinishReason = finishReason;
                            }

                            var usage = jsonPayload["usage"];
                            if (usage != null && usage.Type != JTokenType.Null)
                            {
                                responseMetadata.PromptTokens = usage["prompt_tokens"]?.Value<int?>();
                                responseMetadata.CompletionTokens = usage["completion_tokens"]?.Value<int?>();
                                responseMetadata.TotalTokens = usage["total_tokens"]?.Value<int?>();
                                responseMetadata.IsEstimatedUsage = false;
                            }

                            responseMetadata.ProviderTraceId = GetTraceId(response);
                            if (string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }
                        }
                    }
                }
            }

            var toolCallsResult = new List<ToolCall>();
            foreach (var pair in toolCallAccumulators.OrderBy(item => item.Key))
            {
                toolCallsResult.Add(new ToolCall
                {
                    Id = pair.Value.Id,
                    Name = pair.Value.NameBuilder.ToString(),
                    Input = pair.Value.ArgumentsBuilder.ToString()
                });
            }

            Log.Information(
                "LLM 工具接口响应解析完成。Model={Model}, AssistantContentLength={AssistantContentLength}, ReasoningLength={ReasoningLength}, ToolCallCount={ToolCallCount}",
                model,
                textBuilder.Length,
                reasoningBuilder.Length,
                toolCallsResult.Count);

            return new AgentMessage
            {
                Role = "assistant",
                Content = textBuilder.ToString(),
                ReasoningContent = reasoningBuilder.ToString(),
                ToolCalls = toolCallsResult,
                LlmMetadata = responseMetadata
            };
        }

        public void Dispose()
        {
        }

        private async Task<HttpResponseMessage> SendStreamingRequestWithRetryAsync(
            Uri requestUri,
            string apiKey,
            string requestJson,
            CancellationToken cancellationToken,
            string requestKind,
            string model)
        {
            Exception lastException = null;

            for (var attempt = 1; attempt <= SendRetryCount; attempt++)
            {
                using (var request = CreateStreamingRequest(requestUri, apiKey, requestJson))
                {
                    try
                    {
                        return await SharedHttpClient
                            .SendAsync(
                                request,
                                HttpCompletionOption.ResponseHeadersRead,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                        when (ShouldRetrySendException(ex, attempt, cancellationToken))
                    {
                        lastException = ex;
                        Log.Warning(
                            ex,
                            "调用 LLM {RequestKind} 时遇到瞬时网络异常，准备重试。Endpoint={Endpoint}, Model={Model}, Attempt={Attempt}, MaxAttempt={MaxAttempt}, RequestBodyLength={RequestBodyLength}",
                            requestKind,
                            requestUri,
                            model,
                            attempt,
                            SendRetryCount,
                            GetTextLength(requestJson));
                    }
                }

                await Task.Delay(SendRetryDelay, cancellationToken).ConfigureAwait(false);
            }

            throw lastException ?? new HttpRequestException("调用 LLM 接口失败，且未捕获到底层异常。");
        }

        private static HttpClient CreateSharedHttpClient()
        {
            return new HttpClient(CreateHttpClientHandler())
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        private static HttpClientHandler CreateHttpClientHandler()
        {
            // Word 宿主进程可能被其他 AddIn 或旧默认值影响，局部指定 TLS 1.2，避免修改进程级全局协议。
            return new HttpClientHandler
            {
                SslProtocols = SslProtocols.Tls12
            };
        }

        private static HttpRequestMessage CreateStreamingRequest(Uri requestUri, string apiKey, string requestJson)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            // 兼容 .NET Framework 下的长连接复用问题，避免命中已被对端回收的 keep-alive 连接。
            request.Headers.ConnectionClose = true;
            request.Content = BuildRequestContent(requestJson);
            return request;
        }

        private static bool ShouldRetrySendException(Exception exception, int attempt, CancellationToken cancellationToken)
        {
            return attempt < SendRetryCount
                && !cancellationToken.IsCancellationRequested
                && IsTransientSendException(exception);
        }

        private static bool IsTransientSendException(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is OperationCanceledException)
                {
                    return false;
                }

                if (current is SocketException || current is IOException || current is HttpRequestException)
                {
                    return true;
                }

                var webException = current as WebException;
                if (webException == null)
                {
                    continue;
                }

                switch (webException.Status)
                {
                    case WebExceptionStatus.ConnectFailure:
                    case WebExceptionStatus.ConnectionClosed:
                    case WebExceptionStatus.KeepAliveFailure:
                    case WebExceptionStatus.ReceiveFailure:
                    case WebExceptionStatus.SendFailure:
                    case WebExceptionStatus.Timeout:
                        return true;
                }
            }

            return false;
        }

        internal static TimeSpan ResolveStreamPhaseTimeout(int configuredTimeoutSeconds)
        {
            var effectiveSeconds = configuredTimeoutSeconds > 0 ? configuredTimeoutSeconds : 120;
            if (effectiveSeconds < 15)
            {
                effectiveSeconds = 15;
            }

            return TimeSpan.FromSeconds(effectiveSeconds);
        }

        private static int GetTextLength(string text)
        {
            return string.IsNullOrEmpty(text) ? 0 : text.Length;
        }

        private static string SummarizeBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return "len=0";
            }

            var normalized = body
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            const int maxPreviewLength = 300;
            if (normalized.Length <= maxPreviewLength)
            {
                return "len=" + normalized.Length + ", preview=" + normalized;
            }

            return "len="
                + normalized.Length
                + ", preview="
                + normalized.Substring(0, maxPreviewLength)
                + "...";
        }

        private void ValidateRequest(
            IReadOnlyList<AgentMessage> messages,
            string model,
            out Uri endpoint,
            out string apiKey)
        {
            if (messages == null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentException("模型名称不能为空。", nameof(model));
            }

            var baseUrl = _options.GetBaseUrlForModel(model);
            apiKey = _options.GetApiKeyForModel(model);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("尚未配置可用的 API Key。");
            }

            if (!Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out endpoint))
            {
                throw new InvalidOperationException("BaseUrl 不是有效的绝对地址。");
            }
        }

        private static StringContent BuildRequestContent(string requestJson)
        {
            return new StringContent(
                requestJson ?? string.Empty,
                Encoding.UTF8,
                "application/json");
        }

        private static string BuildRequestJson(
            string model,
            IReadOnlyList<AgentMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            ModelCapability capability)
        {
            var normalizedMessages = NormalizeMessagesForProvider(messages);
            ValidateMessagesForProvider(normalizedMessages);

            var payload = new JObject
            {
                ["model"] = model,
                ["stream"] = true,
                ["messages"] = BuildMessagesPayload(normalizedMessages, capability)
            };

            if (tools != null && tools.Count > 0)
            {
                payload["tools"] = BuildToolsPayload(tools);
                payload["tool_choice"] = "auto";
            }

            return payload.ToString(Formatting.None);
        }

        private static void ValidateMessagesForProvider(IReadOnlyList<AgentMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                throw new InvalidOperationException("LLM 请求 messages 不能为空。");
            }

            var hasUserQuery = messages.Any(message =>
                message != null
                && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && !message.IsInternalObservation
                && !string.IsNullOrWhiteSpace(message.Content));
            if (!hasUserQuery)
            {
                throw new InvalidOperationException("LLM 请求 messages 缺少有效的真实 role=user 用户消息，已阻止发送非法请求。");
            }

            HashSet<string> pendingToolCallIds = null;
            foreach (var message in messages)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Role))
                {
                    continue;
                }

                var role = message.Role.Trim().ToLowerInvariant();
                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureNoPendingToolCalls(pendingToolCallIds);
                    var validToolCallIds = message.ToolCalls == null
                        ? new List<string>()
                        : message.ToolCalls
                            .Where(toolCall => toolCall != null
                                && !string.IsNullOrWhiteSpace(toolCall.Id)
                                && !string.IsNullOrWhiteSpace(toolCall.Name))
                            .Select(toolCall => toolCall.Id)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                    pendingToolCallIds = validToolCallIds.Count == 0
                        ? null
                        : new HashSet<string>(validToolCallIds, StringComparer.Ordinal);
                    continue;
                }

                if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(message.ToolCallId))
                    {
                        throw new InvalidOperationException("LLM 请求 messages 包含缺少 tool_call_id 的 tool 消息，已阻止发送非法请求。");
                    }

                    if (pendingToolCallIds == null || !pendingToolCallIds.Remove(message.ToolCallId))
                    {
                        throw new InvalidOperationException("LLM 请求 messages 包含孤立的 tool 消息，已阻止发送非法请求。");
                    }

                    if (pendingToolCallIds.Count == 0)
                    {
                        pendingToolCallIds = null;
                    }

                    continue;
                }

                EnsureNoPendingToolCalls(pendingToolCallIds);
                pendingToolCallIds = null;
            }

            EnsureNoPendingToolCalls(pendingToolCallIds);
        }

        private static void EnsureNoPendingToolCalls(HashSet<string> pendingToolCallIds)
        {
            if (pendingToolCallIds != null && pendingToolCallIds.Count > 0)
            {
                throw new InvalidOperationException("LLM 请求 messages 包含未闭合的 assistant tool_calls，已阻止发送非法请求。");
            }
        }

        private static JArray BuildMessagesPayload(
            IReadOnlyList<AgentMessage> messages,
            ModelCapability capability)
        {
            var payload = new JArray();
            foreach (var message in NormalizeMessagesForProvider(messages))
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Role))
                {
                    continue;
                }

                var role = message.Role.Trim().ToLowerInvariant();
                var messagePayload = new JObject
                {
                    ["role"] = role
                };

                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                    && message.ToolCalls != null
                    && message.ToolCalls.Count > 0)
                {
                    if (capability != null
                        && capability.RequiresReasoningContentReplay
                        && !string.IsNullOrWhiteSpace(message.ReasoningContent))
                    {
                        // DeepSeek V3.x 在工具链路中要求回放 reasoning_content。
                        messagePayload["reasoning_content"] = message.ReasoningContent;
                    }

                    var toolCalls = new JArray();
                    foreach (var toolCall in message.ToolCalls)
                    {
                        if (toolCall == null
                            || string.IsNullOrWhiteSpace(toolCall.Id)
                            || string.IsNullOrWhiteSpace(toolCall.Name))
                        {
                            continue;
                        }

                        toolCalls.Add(new JObject
                        {
                            ["id"] = toolCall.Id ?? string.Empty,
                            ["type"] = "function",
                            ["function"] = new JObject
                            {
                                ["name"] = toolCall.Name ?? string.Empty,
                                ["arguments"] = toolCall.Input ?? string.Empty
                            }
                        });
                    }

                    messagePayload["content"] = message.Content ?? string.Empty;
                    if (toolCalls.Count > 0)
                    {
                        messagePayload["tool_calls"] = toolCalls;
                    }
                }
                else if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(message.ToolCallId))
                    {
                        // 缺少 tool_call_id 的 tool 消息不符合兼容协议，直接跳过避免整包被拒绝。
                        continue;
                    }

                    messagePayload["content"] = message.Content ?? string.Empty;
                    messagePayload["tool_call_id"] = message.ToolCallId ?? string.Empty;
                }
                else
                {
                    messagePayload["content"] = message.Content ?? string.Empty;
                }

                payload.Add(messagePayload);
            }

            return payload;
        }

        private static IReadOnlyList<AgentMessage> NormalizeMessagesForProvider(IReadOnlyList<AgentMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return Array.Empty<AgentMessage>();
            }

            var systemMessages = messages
                .Where(message => message != null
                    && string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var nonSystemMessages = messages
                .Where(message => message != null
                    && !string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (systemMessages.Count <= 1)
            {
                return messages;
            }

            var mergedSystemContent = string.Join(
                Environment.NewLine + Environment.NewLine,
                systemMessages
                    .Select(message => string.IsNullOrWhiteSpace(message.Content) ? string.Empty : message.Content.Trim())
                    .Where(content => !string.IsNullOrWhiteSpace(content)));

            var mergedMessages = new List<AgentMessage>(nonSystemMessages.Count + 1)
            {
                new AgentMessage
                {
                    Role = "system",
                    Content = mergedSystemContent
                }
            };
            mergedMessages.AddRange(nonSystemMessages);
            return mergedMessages;
        }

        private static JArray BuildToolsPayload(IReadOnlyList<ToolDefinition> tools)
        {
            var payload = new JArray();
            foreach (var tool in tools)
            {
                if (tool == null)
                {
                    continue;
                }

                JToken parameters;
                try
                {
                    parameters = JToken.Parse(tool.Parameters.GetRawText());
                }
                catch
                {
                    parameters = new JObject();
                }

                payload.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool.Name ?? string.Empty,
                        ["description"] = tool.Description ?? string.Empty,
                        ["parameters"] = parameters
                    }
                });
            }

            return payload;
        }

        private static string BuildMessageSummary(IReadOnlyList<AgentMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return "empty";
            }

            var parts = new List<string>();
            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                if (message == null)
                {
                    parts.Add(i + ":null");
                    continue;
                }

                parts.Add(
                    i
                    + ":role=" + (message.Role ?? string.Empty)
                    + ",contentLen=" + (string.IsNullOrEmpty(message.Content) ? 0 : message.Content.Length)
                    + ",reasoningLen=" + (string.IsNullOrEmpty(message.ReasoningContent) ? 0 : message.ReasoningContent.Length)
                    + ",toolCalls=" + (message.ToolCalls == null ? 0 : message.ToolCalls.Count)
                    + ",toolCallId=" + (string.IsNullOrWhiteSpace(message.ToolCallId) ? "empty" : "set"));
            }

            return string.Join(" | ", parts);
        }

        private static string BuildToolSummary(IReadOnlyList<ToolDefinition> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return "none";
            }

            var names = new List<string>();
            foreach (var tool in tools)
            {
                if (tool != null && !string.IsNullOrWhiteSpace(tool.Name))
                {
                    names.Add(tool.Name);
                }
            }

            return names.Count == 0 ? "none" : string.Join(", ", names);
        }

        private static string FormatHeaders(
            System.Net.Http.Headers.HttpHeaders headers,
            System.Net.Http.Headers.HttpHeaders contentHeaders)
        {
            var parts = new List<string>();
            AppendHeaders(parts, headers);
            AppendHeaders(parts, contentHeaders);
            return parts.Count == 0 ? string.Empty : string.Join("; ", parts);
        }

        private static void AppendHeaders(
            IList<string> parts,
            System.Net.Http.Headers.HttpHeaders headers)
        {
            if (parts == null || headers == null)
            {
                return;
            }

            foreach (var header in headers)
            {
                parts.Add(header.Key + "=" + string.Join(",", header.Value));
            }
        }

        private static string GetTraceId(HttpResponseMessage response)
        {
            if (response == null || response.Headers == null)
            {
                return string.Empty;
            }

            IEnumerable<string> values;
            if (response.Headers.TryGetValues("X-Siliconcloud-Trace-Id", out values))
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return string.Empty;
        }

        private static string BuildChatCompletionsUri(Uri endpoint)
        {
            return endpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions";
        }

        private static async Task<string> ReadLineWithTimeoutAsync(
            StreamReader reader,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var readTask = reader.ReadLineAsync();
            var timeoutTask = Task.Delay(timeout, CancellationToken.None);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            var completedTask = await Task
                .WhenAny(readTask, timeoutTask, cancellationTask)
                .ConfigureAwait(false);

            if (completedTask == readTask)
            {
                return await readTask.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"等待 SSE 数据超时，超过 {timeout.TotalSeconds} 秒。");
        }
    }
}
