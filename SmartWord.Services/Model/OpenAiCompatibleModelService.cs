using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Models;
using SmartWord.Services.Prompts;

namespace SmartWord.Services.Model
{
    public sealed class OpenAiCompatibleModelService : IModelService
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private readonly OpenAiApiOptions _options;
        private readonly PromptCatalogProvider _promptCatalogProvider;

        public OpenAiCompatibleModelService(OpenAiApiOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (!_options.IsConfigured)
            {
                throw new InvalidOperationException("Missing API key. Set SMARTWORD_API_KEY or OPENAI_API_KEY.");
            }

            _promptCatalogProvider = new PromptCatalogProvider(_options.PromptCatalogPath);
        }

        public Task<string> RewriteTextAsync(EditorRewriteRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SelectedText))
            {
                return Task.FromResult(string.Empty);
            }

            var promptPair = _promptCatalogProvider.BuildRewritePrompts(
                ResolvePromptVersion(request.PromptVersion),
                request.Instruction,
                request.SelectedText);

            return ExecuteChatAsync(
                _options.ResolveModel(request.ModelOverride),
                promptPair.SystemPrompt,
                promptPair.UserPrompt,
                0.3d);
        }

        public Task<string> GenerateVbaCodeAsync(VbaGenerationRequest request)
        {
            string entryPoint = request != null && !string.IsNullOrWhiteSpace(request.EntryPoint)
                ? request.EntryPoint
                : "SmartWord_Run";

            string instruction = request == null ? string.Empty : request.Instruction ?? string.Empty;

            var promptPair = _promptCatalogProvider.BuildVbaPrompts(
                ResolvePromptVersion(request == null ? null : request.PromptVersion),
                instruction,
                entryPoint);

            return ExecuteChatAsync(
                _options.ResolveModel(request == null ? null : request.ModelOverride),
                promptPair.SystemPrompt,
                promptPair.UserPrompt,
                0.1d);
        }

        private async Task<string> ExecuteChatAsync(string model, string systemPrompt, string userPrompt, double temperature)
        {
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

            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                using (HttpResponseMessage response = await SharedHttpClient.SendAsync(httpRequest).ConfigureAwait(false))
                {
                    string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            "LLM API request failed (" + (int)response.StatusCode + "): " + TrimForError(responseBody));
                    }

                    var chatResponse = Deserialize<ChatCompletionResponse>(responseBody);
                    if (chatResponse == null ||
                        chatResponse.choices == null ||
                        chatResponse.choices.Length == 0 ||
                        chatResponse.choices[0] == null ||
                        chatResponse.choices[0].message == null ||
                        string.IsNullOrWhiteSpace(chatResponse.choices[0].message.content))
                    {
                        throw new InvalidOperationException("LLM API returned empty content.");
                    }

                    return chatResponse.choices[0].message.content.Trim();
                }
            }
        }

        private string ResolvePromptVersion(string requestPromptVersion)
        {
            if (!string.IsNullOrWhiteSpace(requestPromptVersion))
            {
                return requestPromptVersion.Trim();
            }

            return _options.DefaultPromptVersion;
        }

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
        private sealed class ChatCompletionRequest
        {
            [DataMember(Name = "model")]
            public string model { get; set; }

            [DataMember(Name = "messages")]
            public ChatMessage[] messages { get; set; }

            [DataMember(Name = "temperature")]
            public double temperature { get; set; }
        }

        [DataContract]
        private sealed class ChatMessage
        {
            [DataMember(Name = "role")]
            public string role { get; set; }

            [DataMember(Name = "content")]
            public string content { get; set; }
        }

        [DataContract]
        private sealed class ChatCompletionResponse
        {
            [DataMember(Name = "choices")]
            public ChatChoice[] choices { get; set; }
        }

        [DataContract]
        private sealed class ChatChoice
        {
            [DataMember(Name = "message")]
            public ChatMessage message { get; set; }
        }
    }
}
