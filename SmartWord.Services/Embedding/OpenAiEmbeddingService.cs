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

namespace SmartWord.Services.Embedding
{
    public sealed class OpenAiEmbeddingService : IEmbeddingService
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private readonly OpenAiApiOptions _options;

        public OpenAiEmbeddingService(OpenAiApiOptions options)
        {
            _options = options;
        }

        public async Task<float[]> CreateEmbeddingAsync(string input, string modelOverride)
        {
            if (_options == null || !_options.IsConfigured)
            {
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
                        return new float[0];
                    }

                    return embeddingResponse.data[0].embedding;
                }
            }
        }

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

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
            [DataMember(Name = "model")]
            public string model { get; set; }

            [DataMember(Name = "input")]
            public string input { get; set; }
        }

        [DataContract]
        private sealed class EmbeddingResponse
        {
            [DataMember(Name = "data")]
            public EmbeddingItem[] data { get; set; }
        }

        [DataContract]
        private sealed class EmbeddingItem
        {
            [DataMember(Name = "embedding")]
            public float[] embedding { get; set; }
        }
    }
}
