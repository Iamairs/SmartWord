using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示 Plan 模式生成的任务蓝图。
    /// </summary>
    public sealed class ExecutionPlan
    {
        public string TaskDescription { get; set; } = string.Empty;

        public IList<string> TodoList { get; set; } = new List<string>();

        public IList<string> RiskNotes { get; set; } = new List<string>();
    }
}
