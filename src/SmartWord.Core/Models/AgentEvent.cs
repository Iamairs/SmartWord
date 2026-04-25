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
        ChangeExecuted = 9,
        ChangeApplied = 10,
        ChangeUnverified = 11,
        ChangeVerificationFailed = 12,
        ChangeRepairRequired = 13,
        ModeDetected = 14,
        DocumentMismatch = 15,
        DocumentNotWritable = 16,
        Error = 17,
        Cancelled = 18,
        /// <summary>Plan 模式采访阶段：LLM 向用户提问</summary>
        QuestionAsked = 19,
        /// <summary>Plan 模式规划完成：ExecutionPlan 已生成</summary>
        PlanReady = 20,
        /// <summary>Todo Board 已就绪，可供前端首次渲染</summary>
        TodoBoardReady = 21,
        /// <summary>Todo Board 已更新，前端应以完整快照覆盖</summary>
        TodoBoardUpdated = 22,
        /// <summary>系统已注入 Todo reminder</summary>
        TodoReminderInjected = 23,
        /// <summary>Todo Board 进入恢复态，前端必须先提交恢复决策</summary>
        TodoBoardRecoveryRequired = 24,
        /// <summary>Todo Board 因预算耗尽进入暂停态，前端可选择继续、重建或丢弃</summary>
        TodoBoardPaused = 25
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

        /// <summary>QuestionAsked 事件携带的选项列表</summary>
        public string[] QuestionOptions { get; set; }

        /// <summary>PlanReady 事件携带的序列化 ExecutionPlan JSON</summary>
        public string PlanJson { get; set; } = string.Empty;

        /// <summary>Todo 事件携带的序列化 TodoBoard JSON</summary>
        public string BoardJson { get; set; } = string.Empty;

        /// <summary>Todo 事件携带的当前激活任务 Id</summary>
        public string CurrentTodoId { get; set; } = string.Empty;

        /// <summary>恢复决策事件的请求 Id</summary>
        public string RecoveryRequestId { get; set; } = string.Empty;

        /// <summary>恢复决策事件的人类可读原因</summary>
        public string RecoveryReason { get; set; } = string.Empty;

        /// <summary>恢复决策事件中的最近运行结果</summary>
        public string LastRunOutcome { get; set; } = string.Empty;

        /// <summary>恢复决策事件中的最近错误摘要</summary>
        public string LastErrorSummary { get; set; } = string.Empty;

        /// <summary>当前请求是否携带可用于重建的 ActivePlan</summary>
        public bool HasActivePlan { get; set; }

        /// <summary>当前恢复事件是否允许直接恢复旧任务板</summary>
        public bool CanRecoverExisting { get; set; } = true;
    }
}
