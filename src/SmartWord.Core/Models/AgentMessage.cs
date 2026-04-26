using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示兼容 OpenAI 聊天协议的消息模型。
    /// </summary>
    public sealed class AgentMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("reasoning_content")]
        public string ReasoningContent { get; set; } = string.Empty;

        [JsonPropertyName("tool_calls")]
        public List<ToolCall> ToolCalls { get; set; } = new List<ToolCall>();

        [JsonPropertyName("tool_call_id")]
        public string ToolCallId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonIgnore]
        public string LocalMessageId { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsCompressedSummary { get; set; }

        [JsonIgnore]
        public bool IsInternalObservation { get; set; }

        [JsonIgnore]
        public string InternalObservationKind { get; set; } = string.Empty;

        [JsonIgnore]
        public string ToolName { get; set; } = string.Empty;

        [JsonIgnore]
        public string RawToolInput { get; set; } = string.Empty;

        [JsonIgnore]
        public bool ToolSuccess { get; set; }
    }
}
