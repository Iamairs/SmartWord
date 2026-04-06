using System.Text.Json;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示暴露给 LLM 的工具描述。
    /// </summary>
    public sealed class ToolDefinition
    {
        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public JsonElement Parameters { get; set; }
    }
}
