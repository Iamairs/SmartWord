namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示一次工具调用的元数据。
    /// </summary>
    public sealed class ToolCall
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Input { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;
    }
}
