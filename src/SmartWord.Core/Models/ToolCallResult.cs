namespace SmartWord.Core.Models
{
    /// <summary>
    /// 统一封装工具执行结果，便于前后端事件复用。
    /// </summary>
    public sealed class ToolCallResult
    {
        public bool Success { get; set; }

        public string Output { get; set; } = string.Empty;

        public object Metadata { get; set; }

        public int[] AffectedParagraphs { get; set; }

        public int[] ParagraphRefs { get; set; }

        public static ToolCallResult Ok(string output, int[] affected = null, object metadata = null)
        {
            return new ToolCallResult
            {
                Success = true,
                Output = output,
                AffectedParagraphs = affected,
                Metadata = metadata
            };
        }

        public static ToolCallResult Error(string toolName, string errorMessage)
        {
            return new ToolCallResult
            {
                Success = false,
                Output = "[ERROR in " + toolName + "]" + System.Environment.NewLine + errorMessage
            };
        }

        public static ToolCallResult Denied(string toolName)
        {
            return new ToolCallResult
            {
                Success = false,
                Output = "[PERMISSION DENIED] Tool '" + toolName + "' is not allowed in current mode."
            };
        }

        public static ToolCallResult Skipped(string toolName)
        {
            return new ToolCallResult
            {
                Success = false,
                Output = "[SKIPPED] Tool '" + toolName + "' was skipped by user."
            };
        }
    }
}
