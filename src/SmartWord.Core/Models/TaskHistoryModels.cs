using System;
using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示一次 SmartWord 任务运行的最终状态。
    /// </summary>
    public enum TaskRunStatus
    {
        Running = 0,
        Completed = 1,
        Failed = 2,
        Cancelled = 3,
        Paused = 4
    }

    /// <summary>
    /// 创建任务历史记录时需要的基础信息。
    /// </summary>
    public sealed class TaskRunStartRequest
    {
        public string DocumentPath { get; set; } = string.Empty;

        public string UserGoal { get; set; } = string.Empty;

        public string Mode { get; set; } = string.Empty;

        public string PermissionMode { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public int CompletedSteps { get; set; }

        public int TotalSteps { get; set; }
    }

    /// <summary>
    /// 任务运行摘要，用于最近历史列表。
    /// </summary>
    public sealed class TaskRunRecord
    {
        public string Id { get; set; } = string.Empty;

        public string DocumentPath { get; set; } = string.Empty;

        public string DocumentKey { get; set; } = string.Empty;

        public string UserGoal { get; set; } = string.Empty;

        public string Mode { get; set; } = string.Empty;

        public string PermissionMode { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public TaskRunStatus Status { get; set; } = TaskRunStatus.Running;

        public DateTimeOffset StartedAtUtc { get; set; }

        public DateTimeOffset? EndedAtUtc { get; set; }

        public string Summary { get; set; } = string.Empty;

        public string FailureReason { get; set; } = string.Empty;

        public string CancelReason { get; set; } = string.Empty;

        public int CompletedSteps { get; set; }

        public int TotalSteps { get; set; }

        public int ToolCount { get; set; }

        public int ChangeCount { get; set; }

        public int VerifiedChangeCount { get; set; }
    }

    /// <summary>
    /// 记录一次工具调用的完整审计信息。
    /// </summary>
    public sealed class TaskToolRecord
    {
        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public string OperationDescription { get; set; } = string.Empty;

        public string RawInput { get; set; } = string.Empty;

        public string Output { get; set; } = string.Empty;

        public bool Success { get; set; }

        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 记录一次文档改动的状态演进。
    /// </summary>
    public sealed class TaskChangeRecord
    {
        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public string OperationDescription { get; set; } = string.Empty;

        public int[] AffectedParagraphs { get; set; } = new int[0];

        public string Status { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 结束任务运行时写入的汇总信息。
    /// </summary>
    public sealed class TaskRunCompletion
    {
        public TaskRunStatus Status { get; set; }

        public string Summary { get; set; } = string.Empty;

        public string FailureReason { get; set; } = string.Empty;

        public string CancelReason { get; set; } = string.Empty;

        public int CompletedSteps { get; set; }

        public int TotalSteps { get; set; }

        public DateTimeOffset EndedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 任务历史详情，包含运行摘要、工具调用和文档改动。
    /// </summary>
    public sealed class TaskRunDetail
    {
        public TaskRunRecord Run { get; set; }

        public IReadOnlyList<TaskToolRecord> Tools { get; set; } = new List<TaskToolRecord>();

        public IReadOnlyList<TaskChangeRecord> Changes { get; set; } = new List<TaskChangeRecord>();
    }
}
