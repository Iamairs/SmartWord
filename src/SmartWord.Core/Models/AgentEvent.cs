using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示前后端之间流转的事件类型。
    /// </summary>
    public enum AgentEventType
    {
        StreamChunk = 0,
        ToolCallStarted = 1,
        ToolCallCompleted = 2,
        ToolCallDenied = 3,
        ToolCallSkipped = 4,
        ContextCompacted = 5,
        TaskCompleted = 6,
        MaxIterationsReached = 7,
        ProgressUpdate = 8,
        ChangeApplied = 9,
        ModeDetected = 10,
        DocumentMismatch = 11,
        DocumentNotWritable = 12,
        Error = 13,
        Cancelled = 14
    }

    /// <summary>
    /// 表示一次可以发送给前端或下游处理器的事件负载。
    /// </summary>
    public sealed class AgentEvent
    {
        public AgentEventType Type { get; set; }

        public string Content { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public string ToolInput { get; set; } = string.Empty;

        public string ToolOutput { get; set; } = string.Empty;

        public bool ToolSuccess { get; set; }

        public bool RequiresConfirmation { get; set; }

        public string ToolCallId { get; set; } = string.Empty;

        public int[] ParagraphRefs { get; set; }

        public int[] AffectedParagraphs { get; set; }

        public string OperationDescription { get; set; } = string.Empty;

        public int CompletedSteps { get; set; }

        public int TotalSteps { get; set; }

        public string DetectedMode { get; set; } = string.Empty;

        public bool IsAutoRouted { get; set; }

        public string Message { get; set; } = string.Empty;

        public List<CitationEntry> Citations { get; set; } = new List<CitationEntry>();
    }
}
