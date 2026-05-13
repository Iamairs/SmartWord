using System.Text.Json.Serialization;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 保存一次真实 LLM 响应返回的可观测元数据。
    /// </summary>
    public sealed class LlmResponseMetadata
    {
        [JsonIgnore]
        public int? PromptTokens { get; set; }

        [JsonIgnore]
        public int? CompletionTokens { get; set; }

        [JsonIgnore]
        public int? TotalTokens { get; set; }

        [JsonIgnore]
        public int? EstimatedPromptTokens { get; set; }

        [JsonIgnore]
        public int? EstimatedCompletionTokens { get; set; }

        [JsonIgnore]
        public bool IsEstimatedUsage { get; set; }

        [JsonIgnore]
        public string FinishReason { get; set; } = string.Empty;

        [JsonIgnore]
        public string ProviderTraceId { get; set; } = string.Empty;
    }
}
