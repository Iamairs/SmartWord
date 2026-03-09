using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Models;
using SmartWord.Core.Models.Conversation;
using SmartWord.Services.Logging;
using SmartWord.Services.Prompts;

// 文件说明：
// OpenAI 兼容模型服务实现，负责根据 Prompt 模板发起对话请求并解析模型输出。
namespace SmartWord.Services.Model
{
    /// <summary>
    /// OpenAI 兼容模型服务。
    /// </summary>
    public sealed class OpenAiCompatibleModelService : IModelService
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private readonly OpenAiApiOptions _options;
        private readonly PromptCatalogProvider _promptCatalogProvider;
        private readonly IAppLogger _logger;

        /// <summary>
        /// 初始化模型服务并加载 Prompt 目录。
        /// </summary>
        /// <param name="options">API 配置。</param>
        public OpenAiCompatibleModelService(OpenAiApiOptions options, IAppLogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? NullAppLogger.Instance;
            if (!_options.IsConfigured)
            {
                throw new InvalidOperationException("Missing API key. Set SMARTWORD_API_KEY or OPENAI_API_KEY.");
            }

            _promptCatalogProvider = new PromptCatalogProvider(_options.PromptCatalogPath);
        }

        /// <summary>
        /// 调用模型执行文本改写。
        /// </summary>
        /// <param name="request">改写请求。</param>
        /// <returns>改写结果文本。</returns>
        public Task<string> RewriteTextAsync(EditorRewriteRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SelectedText))
            {
                return Task.FromResult(string.Empty);
            }

            var promptPair = _promptCatalogProvider.BuildWritingPrompts(
                ResolvePromptVersion(request.PromptVersion),
                request.Instruction,
                request.SelectedText);

            return ExecuteChatAsync(
                _options.ResolveModel(request.ModelOverride),
                promptPair.SystemPrompt,
                promptPair.UserPrompt,
                0.3d,
                cancellationToken);
        }

        /// <summary>
        /// 调用模型生成 VBA 代码。
        /// </summary>
        /// <param name="request">VBA 生成请求。</param>
        /// <returns>VBA 代码文本。</returns>
        public Task<string> GenerateVbaCodeAsync(VbaGenerationRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            string entryPoint = request != null && !string.IsNullOrWhiteSpace(request.EntryPoint)
                ? request.EntryPoint
                : "SmartWord_Run";

            string instruction = request == null ? string.Empty : request.Instruction ?? string.Empty;

            var promptPair = _promptCatalogProvider.BuildExecutePrompts(
                ResolvePromptVersion(request == null ? null : request.PromptVersion),
                instruction,
                request == null ? string.Empty : request.SelectedText,
                entryPoint,
                string.Empty);

            return ExecuteChatAsync(
                _options.ResolveModel(request == null ? null : request.ModelOverride),
                promptPair.SystemPrompt,
                promptPair.UserPrompt,
                0.1d,
                cancellationToken);
        }

        /// <summary>
        /// 调用模型生成文档问答结果。
        /// </summary>
        /// <param name="request">问答请求。</param>
        /// <returns>问答结果文本。</returns>
        public Task<string> AnswerQuestionAsync(DocumentQaRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Question))
            {
                return Task.FromResult(string.Empty);
            }

            var promptPair = _promptCatalogProvider.BuildQaPrompts(
                ResolvePromptVersion(request.PromptVersion),
                request.Question,
                request.SelectedText,
                request.RetrievedContext);

            return ExecuteChatAsync(
                _options.ResolveModel(request.ModelOverride),
                promptPair.SystemPrompt,
                promptPair.UserPrompt,
                0.2d,
                cancellationToken);
        }

        /// <summary>
        /// 透传系统/用户提示词到聊天完成接口。
        /// </summary>
        /// <param name="systemPrompt">系统提示词。</param>
        /// <param name="userPrompt">用户提示词。</param>
        /// <param name="modelOverride">模型覆盖项。</param>
        /// <param name="temperature">采样温度。</param>
        /// <returns>模型响应文本。</returns>
        public Task<string> ChatWithPromptsAsync(string systemPrompt, string userPrompt, string modelOverride, double temperature, CancellationToken cancellationToken = default(CancellationToken))
        {
            return ExecuteChatAsync(
                _options.ResolveModel(modelOverride),
                systemPrompt ?? string.Empty,
                userPrompt ?? string.Empty,
                temperature,
                cancellationToken);
        }

        /// <summary>
        /// 发起聊天完成请求并返回首条响应内容。
        /// </summary>
        private async Task<string> ExecuteChatAsync(string model, string systemPrompt, string userPrompt, double temperature, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();

            var request = new ChatCompletionRequest
            {
                model = model,
                temperature = temperature,
                messages = new[]
                {
                    new ChatMessage { role = "system", content = systemPrompt },
                    new ChatMessage { role = "user", content = userPrompt }
                }
            };

            string url = _options.BaseUrl + "/chat/completions";
            string requestJson = Serialize(request);
            _logger.Info(
                "llm.request.start",
                "Sending LLM request. Model={Model} Url={Url} Temperature={Temperature} SystemPrompt={SystemPrompt} UserPrompt={UserPrompt}",
                model,
                url,
                temperature,
                systemPrompt,
                userPrompt);

            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
            {
                // 采用标准 Bearer 鉴权，兼容 OpenAI 及兼容网关。
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                using (HttpResponseMessage response = await SharedHttpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false))
                {
                    string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    stopwatch.Stop();
                    _logger.Info(
                        "llm.request.end",
                        "Received LLM response. Model={Model} StatusCode={StatusCode} DurationMs={DurationMs} ResponseLength={ResponseLength}",
                        model,
                        (int)response.StatusCode,
                        stopwatch.ElapsedMilliseconds,
                        string.IsNullOrWhiteSpace(responseBody) ? 0 : responseBody.Length);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.Error(
                            "llm.request.failed",
                            null,
                            "LLM API returned failure. Model={Model} StatusCode={StatusCode} Body={Body}",
                            model,
                            (int)response.StatusCode,
                            TrimForError(responseBody));
                        throw new InvalidOperationException(
                            "LLM API request failed (" + (int)response.StatusCode + "): " + TrimForError(responseBody));
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    var chatResponse = Deserialize<ChatCompletionResponse>(responseBody);
                    if (chatResponse == null ||
                        chatResponse.choices == null ||
                        chatResponse.choices.Length == 0 ||
                        chatResponse.choices[0] == null ||
                        chatResponse.choices[0].message == null ||
                        string.IsNullOrWhiteSpace(chatResponse.choices[0].message.content))
                    {
                        // 协议成功但无有效内容时主动抛错，便于上层统一处理。
                        _logger.Warn("llm.response.empty", "LLM returned empty content. Model={Model}", model);
                        throw new InvalidOperationException("LLM API returned empty content.");
                    }

                    string content = chatResponse.choices[0].message.content.Trim();
                    _logger.Debug("llm.response.parsed", "Parsed LLM response. Model={Model} ContentLength={ContentLength}", model, content.Length);
                    return content;
                }
            }
        }

        /// <summary>
        /// 解析最终使用的 Prompt 版本。
        /// </summary>
        /// <param name="requestPromptVersion">请求中的 Prompt 版本。</param>
        /// <returns>有效 Prompt 版本。</returns>
        private string ResolvePromptVersion(string requestPromptVersion)
        {
            if (!string.IsNullOrWhiteSpace(requestPromptVersion))
            {
                return requestPromptVersion.Trim();
            }

            return _options.DefaultPromptVersion;
        }

        /// <summary>
        /// 截断错误响应文本，避免异常消息过长。
        /// </summary>
        /// <param name="input">原始错误文本。</param>
        /// <returns>截断后的文本。</returns>
        private static string TrimForError(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "empty response body";
            }

            string trimmed = input.Trim();
            if (trimmed.Length <= 300)
            {
                return trimmed;
            }

            return trimmed.Substring(0, 300) + "...";
        }

        /// <summary>
        /// 将对象序列化为 JSON 字符串。
        /// </summary>
        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>
        /// 将 JSON 反序列化为目标对象。
        /// </summary>
        private static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(bytes))
            {
                return serializer.ReadObject(stream) as T;
            }
        }

        [DataContract]
        private sealed class ChatCompletionRequest
        {
            /// <summary>
            /// 模型名称。
            /// </summary>
            [DataMember(Name = "model")]
            public string model { get; set; }

            /// <summary>
            /// 对话消息数组。
            /// </summary>
            [DataMember(Name = "messages")]
            public ChatMessage[] messages { get; set; }

            /// <summary>
            /// 采样温度。
            /// </summary>
            [DataMember(Name = "temperature")]
            public double temperature { get; set; }
        }

        [DataContract]
        private sealed class ChatMessage
        {
            /// <summary>
            /// 消息角色。
            /// </summary>
            [DataMember(Name = "role")]
            public string role { get; set; }

            /// <summary>
            /// 消息文本。
            /// </summary>
            [DataMember(Name = "content")]
            public string content { get; set; }
        }

        [DataContract]
        private sealed class ChatCompletionResponse
        {
            /// <summary>
            /// 响应候选集合。
            /// </summary>
            [DataMember(Name = "choices")]
            public ChatChoice[] choices { get; set; }
        }

        [DataContract]
        private sealed class ChatChoice
        {
            /// <summary>
            /// 候选消息。
            /// </summary>
            [DataMember(Name = "message")]
            public ChatMessage message { get; set; }
        }
    }
}
