using System;
using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示一个文档级的 Todo 任务板快照。
    /// </summary>
    public sealed class TodoBoard
    {
        public int SchemaVersion { get; set; } = 1;

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
}
