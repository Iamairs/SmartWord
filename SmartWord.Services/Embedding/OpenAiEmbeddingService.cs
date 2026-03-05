using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Services.Model;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

// 文件说明：
// OpenAI 兼容向量服务实现，负责调用 embeddings 接口并解析向量结果。
namespace SmartWord.Services.Embedding
{
    /// <summary>
    /// 远端向量服务（OpenAI 兼容协议）。
    /// </summary>
    public sealed class OpenAiEmbeddingService : IEmbeddingService
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private readonly OpenAiApiOptions _options;

        /// <summary>
        /// 初始化远端向量服务。
        /// </summary>
        /// <param name="options">API 配置。</param>
        public OpenAiEmbeddingService(OpenAiApiOptions options)
        {
            _options = options;
        }

        /// <summary>
        /// 调用远端接口生成向量。
        /// </summary>
        /// <param name="input">输入文本。</param>
        /// <param name="modelOverride">模型覆盖项。</param>
        /// <returns>向量数组；在未配置或响应异常时返回空数组。</returns>
        public async Task<float[]> CreateEmbeddingAsync(string input, string modelOverride)
        {
            if (_options == null || !_options.IsConfigured)
            {
                // 未配置密钥时直接返回空向量，由上层走降级策略。
                return new float[0];
            }

            string payload = Serialize(new EmbeddingRequest
            {
                model = string.IsNullOrWhiteSpace(modelOverride) ? _options.EmbeddingModel : modelOverride,
                input = input ?? string.Empty
            });

            string url = _options.BaseUrl + "/embeddings";
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                using (HttpResponseMessage response = await SharedHttpClient.SendAsync(request).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("Embedding API request failed (" + (int)response.StatusCode + "): " + body);
                    }

                    EmbeddingResponse embeddingResponse = Deserialize<EmbeddingResponse>(body);
                    if (embeddingResponse == null || embeddingResponse.data == null || embeddingResponse.data.Length == 0 || embeddingResponse.data[0] == null || embeddingResponse.data[0].embedding == null)
                    {
                        // 协议合法但无数据时返回空向量，避免上层空引用。
                        return new float[0];
                    }

                    return embeddingResponse.data[0].embedding;
                }
            }
        }

        /// <summary>
        /// 将对象序列化为 JSON 字符串。
        /// </summary>
        /// <typeparam name="T">对象类型。</typeparam>
        /// <param name="value">待序列化对象。</param>
        /// <returns>JSON 文本。</returns>
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
        /// <typeparam name="T">目标类型。</typeparam>
        /// <param name="json">JSON 文本。</param>
        /// <returns>反序列化对象；失败时返回空值。</returns>
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
        private sealed class EmbeddingRequest
        {
            /// <summary>
            /// 向量模型名称。
            /// </summary>
            [DataMember(Name = "model")]
            public string model { get; set; }

            /// <summary>
            /// 待向量化文本。
            /// </summary>
            [DataMember(Name = "input")]
            public string input { get; set; }
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
            /// 向量值数组。
            /// </summary>
            [DataMember(Name = "embedding")]
            public float[] embedding { get; set; }
        }
    }
}
