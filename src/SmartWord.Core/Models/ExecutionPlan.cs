using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    public enum TodoItemStatus { Pending, InProgress, Completed, Failed, Skipped }

    public sealed class TodoItem
    {
        public string Description { get; set; } = string.Empty;
        public TodoItemStatus Status { get; set; } = TodoItemStatus.Pending;
    }

    /// <summary>
    /// 表示 Plan 模式生成的任务蓝图。
    /// </summary>
    public sealed class ExecutionPlan
    {
        public string TaskDescription { get; set; } = string.Empty;

        public IList<TodoItem> TodoList { get; set; } = new List<TodoItem>();

        public IList<string> RiskNotes { get; set; } = new List<string>();
    }
}
