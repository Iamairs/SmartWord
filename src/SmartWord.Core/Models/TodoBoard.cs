using System;
using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示 Todo Board 当前所处的执行状态。
    /// </summary>
    public enum TodoBoardExecutionState
    {
        Idle = 0,
        Running = 1,
        RecoveryRequired = 2,
        Paused = 3
    }

    /// <summary>
    /// 表示最近一次 Agent 运行在 Todo Board 视角下的结束结果。
    /// </summary>
    public enum TodoBoardRunOutcome
    {
        None = 0,
        Succeeded = 1,
        Cancelled = 2,
        Failed = 3,
        RolledBack = 4,
        CrashedLike = 5,
        PausedByBudget = 6
    }

    /// <summary>
    /// 表示前端给出的 Todo Board 恢复决策。
    /// </summary>
    public enum TodoBoardRecoveryDecision
    {
        RecoverExisting = 0,
        RebuildFromActivePlan = 1,
        DiscardAndCreateEmpty = 2,
        SkipCurrentTodo = 3
    }

    /// <summary>
    /// 表示一次运行开始前的 Todo Board 准备结果。
    /// </summary>
    public enum TodoBoardPreparationStatus
    {
        Ready = 0,
        RecoveryRequired = 1,
        Paused = 2
    }

    /// <summary>
    /// 表示一个文档级的 Todo 任务板快照。
    /// </summary>
    public sealed class TodoBoard
    {
        public const int CurrentSchemaVersion = 4;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string BoardId { get; set; } = string.Empty;

        public string DocumentPath { get; set; } = string.Empty;

        public int Version { get; set; } = 1;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public int RoundsSinceLastTodoUpdate { get; set; }

        public int LastReminderRound { get; set; }

        public int RoundsSinceLastReminder { get; set; }

        public int ReminderCount { get; set; }

        public bool HasPendingWriteWithoutTodoWrite { get; set; }

        public int RoundsSincePendingWriteWithoutTodoWrite { get; set; }

        public bool HasInjectedPendingWriteReminder { get; set; }

        public TodoBoardExecutionState ExecutionState { get; set; } = TodoBoardExecutionState.Idle;

        public string LastRunId { get; set; } = string.Empty;

        public DateTime? LastRunStartedAtUtc { get; set; }

        public DateTime? LastRunFinishedAtUtc { get; set; }

        public TodoBoardRunOutcome LastRunOutcome { get; set; } = TodoBoardRunOutcome.None;

        public string LastErrorSummary { get; set; } = string.Empty;

        public string RecoveryReason { get; set; } = string.Empty;

        public string PauseReason { get; set; } = string.Empty;

        public string SourcePlanFingerprint { get; set; } = string.Empty;

        public DateTime? LastTrustedCheckpointAtUtc { get; set; }

        public string LastTrustedCheckpointSummary { get; set; } = string.Empty;

        /// <summary>
        /// 最近一次可信提交后的 Todo Board 快照。
        /// 该快照不包含运行中的临时写步骤信息，用于回滚、暂停与恢复时还原稳定进度。
        /// </summary>
        public string LastCommittedBoardSnapshotJson { get; set; } = string.Empty;

        public string InFlightWriteStepId { get; set; } = string.Empty;

        public string InFlightWriteStepSummary { get; set; } = string.Empty;

        public string InFlightTodoBoardSnapshotJson { get; set; } = string.Empty;

        public DateTime? InFlightStartedAtUtc { get; set; }

        public IList<TodoBoardItem> Items { get; set; } = new List<TodoBoardItem>();
    }

    /// <summary>
    /// 表示任务板中的单个任务项。
    /// </summary>
    public sealed class TodoBoardItem
    {
        public string Id { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public TodoItemStatus Status { get; set; } = TodoItemStatus.Pending;

        public int Order { get; set; }

        public string Notes { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? CompletedAt { get; set; }
    }

    /// <summary>
    /// 表示任务板统计摘要。
    /// </summary>
    public sealed class TodoBoardStats
    {
        public int TotalCount { get; set; }

        public int PendingCount { get; set; }

        public int InProgressCount { get; set; }

        public int CompletedCount { get; set; }

        public int FailedCount { get; set; }

        public int SkippedCount { get; set; }

        public int HandledCount { get; set; }

        public string CurrentTodoId { get; set; } = string.Empty;

        public string CurrentTodoContent { get; set; } = string.Empty;
    }

    /// <summary>
    /// 表示 TodoWrite 工具的一次写入请求。
    /// </summary>
    public sealed class TodoWriteRequest
    {
        public string Action { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public string Notes { get; set; } = string.Empty;

        public TodoItemStatus? Status { get; set; }

        public int? Order { get; set; }

        public IList<string> OrderedIds { get; set; } = new List<string>();

        public IList<TodoBoardItem> Items { get; set; } = new List<TodoBoardItem>();
    }

    /// <summary>
    /// 表示一次 Todo 读写后的统一输出结果。
    /// </summary>
    public sealed class TodoWriteResult
    {
        public bool Success { get; set; }

        public string Operation { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public string UpdatedItemId { get; set; } = string.Empty;

        public string CurrentTodoId { get; set; } = string.Empty;

        public TodoBoard Board { get; set; }

        public TodoBoardStats Stats { get; set; } = new TodoBoardStats();

        public string MarkdownView { get; set; } = string.Empty;

        public string BoardJson { get; set; } = string.Empty;
    }

    /// <summary>
    /// 供编排器识别 Todo 工具结果的元数据。
    /// </summary>
    public sealed class TodoToolMetadata
    {
        public bool IsWriteOperation { get; set; }

        public string Operation { get; set; } = string.Empty;

        public string BoardJson { get; set; } = string.Empty;

        public string CurrentTodoId { get; set; } = string.Empty;

        public int CompletedSteps { get; set; }

        public int TotalSteps { get; set; }
    }

    /// <summary>
    /// 表示运行前 Todo Board 的准备结果。
    /// </summary>
    public sealed class TodoBoardPreparationResult
    {
        public TodoBoardPreparationStatus Status { get; set; } = TodoBoardPreparationStatus.Ready;

        public TodoBoard Board { get; set; }

        public string RecoveryReason { get; set; } = string.Empty;

        public string PauseReason { get; set; } = string.Empty;

        public TodoBoardRunOutcome LastRunOutcome { get; set; } = TodoBoardRunOutcome.None;

        public string LastErrorSummary { get; set; } = string.Empty;

        public bool HasActivePlan { get; set; }

        public string ActivePlanFingerprint { get; set; } = string.Empty;

        public bool CanRecoverExisting { get; set; } = true;
    }

    /// <summary>
    /// 表示一次 Todo Board 推送事件的更新语义。
    /// </summary>
    public enum TodoBoardUpdateKind
    {
        Unknown = 0,
        Ready = 1,
        ToolReadSync = 2,
        ToolWriteSync = 3,
        RollbackRestored = 4,
        Reminder = 5,
        PausedSnapshot = 6,
        RecoverySnapshot = 7
    }
}
