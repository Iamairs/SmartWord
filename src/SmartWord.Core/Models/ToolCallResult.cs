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

        public string OperationDescription { get; set; } = string.Empty;

        public static ToolCallResult Ok(
            string output,
            int[] affected = null,
            object metadata = null,
            string operationDescription = null)
        {
            return new ToolCallResult
            {
                Success = true,
                Output = output,
                AffectedParagraphs = affected,
                Metadata = metadata,
                OperationDescription = operationDescription ?? string.Empty
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

        public static ToolCallResult Denied(string toolName, string errorMessage = null)
        {
            return new ToolCallResult
            {
                Success = false,
                Output = string.IsNullOrWhiteSpace(errorMessage)
                    ? "[PERMISSION DENIED] Tool '" + toolName + "' is not allowed in current mode."
                    : "[PERMISSION DENIED] Tool '" + toolName + "' was blocked."
                        + System.Environment.NewLine
                        + errorMessage
            };
        }

        public static ToolCallResult Skipped(string toolName, string message = null)
        {
            return new ToolCallResult
            {
                Success = false,
                Output = string.IsNullOrWhiteSpace(message)
                    ? "[SKIPPED] Tool '" + toolName + "' was skipped by user."
                    : "[SKIPPED] Tool '" + toolName + "' was skipped by user."
                        + System.Environment.NewLine
                        + message
            };
        }
    }
}
