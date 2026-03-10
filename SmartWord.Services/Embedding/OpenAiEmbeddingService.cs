using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Services.Logging;
using SmartWord.Services.Model;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// 文件说明：
// OpenAI 兼容向量服务实现，支持单条与批量向量生成。
namespace SmartWord.Services.Embedding
{
    /// <summary>
    /// 远端向量服务（OpenAI 兼容协议）。
    /// </summary>
    public sealed class OpenAiEmbeddingService : IBatchEmbeddingService
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private readonly OpenAiApiOptions _options;
        private readonly IAppLogger _logger;

        /// <summary>
        /// 初始化远端向量服务。
        /// </summary>
        /// <param name="options">API 配置。</param>
        /// <param name="logger">日志服务。</param>
        public OpenAiEmbeddingService(OpenAiApiOptions options, IAppLogger logger)
        {
            _options = options;
            _logger = logger ?? NullAppLogger.Instance;
        }

        /// <summary>
        /// 调用远端接口生成单条向量。
        /// </summary>
        /// <param name="input">输入文本。</param>
        /// <param name="modelOverride">模型覆盖项（保留参数，当前 Embedding 流程不使用）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>向量数组；在未配置时返回空数组。</returns>
        public async Task<float[]> CreateEmbeddingAsync(string input, string modelOverride, CancellationToken cancellationToken = default(CancellationToken))
        {
            IReadOnlyList<float[]> vectors = await CreateEmbeddingsAsync(
                new[] { input ?? string.Empty },
                modelOverride,
                cancellationToken).ConfigureAwait(false);

            if (vectors == null || vectors.Count == 0 || vectors[0] == null)
            {
                return new float[0];
            }

            return vectors[0];
        }

        /// <summary>
        /// 调用远端接口批量生成向量。
        /// </summary>
        /// <param name="inputs">输入文本集合。</param>
        /// <param name="modelOverride">模型覆盖项（保留参数，当前 Embedding 流程不使用）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>向量数组集合，顺序与输入一致。</returns>
        public async Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(IReadOnlyList<string> inputs, string modelOverride, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (inputs == null || inputs.Count == 0)
            {
                return new List<float[]>();
            }

            if (_options == null || !_options.IsEmbeddingConfigured)
            {
                _logger.Warn("embedding.skipped", "Embedding request skipped because embedding service is not configured.");
                var empty = new List<float[]>(inputs.Count);
                for (int i = 0; i < inputs.Count; i++)
                {
                    empty.Add(new float[0]);
                }

                return empty;
            }

            cancellationToken.ThrowIfCancellationRequested();
            string url = _options.ResolveEmbeddingBaseUrl() + "/embeddings";
            var stopwatch = Stopwatch.StartNew();

            string[] payloadInputs = new string[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                payloadInputs[i] = inputs[i] ?? string.Empty;
            }

            string payload = Serialize(new BatchEmbeddingRequest
            {
                model = _options.EmbeddingModel,
                input = payloadInputs
            });

            _logger.Info(
                "embedding.request.start",
                "Sending embedding request. Model={Model} Url={Url} InputCount={InputCount}",
                _options.EmbeddingModel,
                url,
                payloadInputs.Length);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ResolveEmbeddingApiKey());
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using (HttpResponseMessage response = await SharedHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    stopwatch.Stop();

                    _logger.Info(
                        "embedding.request.end",
                        "Embedding response received. Model={Model} StatusCode={StatusCode} DurationMs={DurationMs} InputCount={InputCount} ResponseLength={ResponseLength}",
                        _options.EmbeddingModel,
                        (int)response.StatusCode,
                        stopwatch.ElapsedMilliseconds,
                        payloadInputs.Length,
                        string.IsNullOrWhiteSpace(body) ? 0 : body.Length);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.Error(
                            "embedding.request.failed",
                            null,
                            "Embedding request failed. Model={Model} StatusCode={StatusCode} Body={Body}",
                            _options.EmbeddingModel,
                            (int)response.StatusCode,
                            body);
                        throw new InvalidOperationException("Embedding API request failed (" + (int)response.StatusCode + "): " + body);
                    }

                    EmbeddingResponse embeddingResponse = Deserialize<EmbeddingResponse>(body);
                    if (embeddingResponse == null || embeddingResponse.data == null || embeddingResponse.data.Length == 0)
                    {
                        _logger.Warn("embedding.response.empty", "Embedding response is empty. Model={Model}", _options.EmbeddingModel);
                        return BuildEmptyVectors(payloadInputs.Length);
                    }

                    var ordered = BuildOrderedVectors(embeddingResponse.data, payloadInputs.Length);
                    _logger.Debug(
                        "embedding.response.parsed",
                        "Embedding parsed. Model={Model} InputCount={InputCount} ParsedCount={ParsedCount}",
                        _options.EmbeddingModel,
                        payloadInputs.Length,
                        ordered.Count);
                    return ordered;
                }
            }
        }

        /// <summary>
        /// 构建与输入顺序一致的向量列表。
        /// </summary>
        private static IReadOnlyList<float[]> BuildOrderedVectors(EmbeddingItem[] items, int expectedCount)
        {
            if (expectedCount <= 0)
            {
                return new List<float[]>();
            }

            var vectors = new List<float[]>(expectedCount);
            for (int i = 0; i < expectedCount; i++)
            {
                vectors.Add(new float[0]);
            }

            for (int i = 0; i < items.Length; i++)
            {
                EmbeddingItem item = items[i];
                if (item == null)
                {
                    continue;
                }

                int index = item.index >= 0 && item.index < expectedCount ? item.index : i;
                if (index >= 0 && index < expectedCount)
                {
                    vectors[index] = item.embedding ?? new float[0];
                }
            }

            return vectors;
        }

        /// <summary>
        /// 构建指定数量的空向量列表。
        /// </summary>
        private static IReadOnlyList<float[]> BuildEmptyVectors(int count)
        {
            var vectors = new List<float[]>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                vectors.Add(new float[0]);
            }

            return vectors;
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
        private sealed class BatchEmbeddingRequest
        {
            /// <summary>
            /// 向量模型名称。
            /// </summary>
            [DataMember(Name = "model")]
            public string model { get; set; }

            /// <summary>
            /// 待向量化文本列表。
            /// </summary>
            [DataMember(Name = "input")]
            public string[] input { get; set; }
        }

        [DataContract]
        private sealed class EmbeddingResponse
        {
            /// <summary>
            /// 返回的数据项集合。
            /// </summary>
            [DataMember(Name = "data")]
            public EmbeddingItem[] data { get; set; }
        }

        [DataContract]
        private sealed class EmbeddingItem
        {
            /// <summary>
            /// 输入索引。
            /// </summary>
            [DataMember(Name = "index")]
            public int index { get; set; }

            /// <summary>
            /// 向量值数组。
            /// </summary>
            [DataMember(Name = "embedding")]
            public float[] embedding { get; set; }
        }
    }
}
