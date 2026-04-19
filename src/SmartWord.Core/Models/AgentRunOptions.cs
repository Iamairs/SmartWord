using SmartWord.Core.Enums;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 定义单次 Agent 运行的可调参数。
    /// </summary>
    public sealed class AgentRunOptions
    {
        public AgentMode Mode { get; set; } = AgentMode.Ask;

        public string Model { get; set; } = string.Empty;

        public int MaxIterations { get; set; } = 8;

        public int CompactionThreshold { get; set; } = 24000;

        public bool RequireConfirmationForScripts { get; set; } = true;

        public bool EnableToolCalling { get; set; } = true;

        public string ModelRoutingMessage { get; set; } = string.Empty;

        public string CustomSystemInstructions { get; set; } = string.Empty;

        /// <summary>Plan→Agent 切换时传入的执行计划，用于进度追踪</summary>
        public ExecutionPlan ActivePlan { get; set; }

        /// <summary>当前已完成的 TodoItem 索引</summary>
        public int CurrentTodoIndex { get; set; } = 0;
    }
}
