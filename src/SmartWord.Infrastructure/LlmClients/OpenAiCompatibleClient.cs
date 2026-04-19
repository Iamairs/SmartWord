using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
                    response = await SharedHttpClient
                        .SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            responseHeadersTimeoutCts.Token)
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
                    response = await SharedHttpClient
                        .SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            responseHeadersTimeoutCts.Token)
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
                ToolCalls = toolCallsResult
            };
        }

        public void Dispose()
        {
        }

        private static HttpClient CreateSharedHttpClient()
        {
            return new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
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
            var payload = new JObject
            {
                ["model"] = model,
                ["stream"] = true,
                ["messages"] = BuildMessagesPayload(messages, capability)
            };

            if (tools != null && tools.Count > 0)
            {
                payload["tools"] = BuildToolsPayload(tools);
                payload["tool_choice"] = "auto";
            }

            return payload.ToString(Formatting.None);
        }

        private static JArray BuildMessagesPayload(
            IReadOnlyList<AgentMessage> messages,
            ModelCapability capability)
        {
            var payload = new JArray();
            foreach (var message in messages)
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
