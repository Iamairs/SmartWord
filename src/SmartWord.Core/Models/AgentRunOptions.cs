using SmartWord.Core.Enums;
using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 定义单次 Agent 运行的可调参数。
    /// </summary>
    public sealed class AgentRunOptions
    {
        public AgentMode Mode { get; set; } = AgentMode.Ask;

        public string Model { get; set; } = string.Empty;

        public int MaxIterations { get; set; } = 100;

        public int CompactionThreshold { get; set; } = 24000;

        public int ContextWindowTokens { get; set; } = 256 * 1024;

        public double ContextSoftLimitRatio { get; set; } = 0.65;

        public double ContextHardLimitRatio { get; set; } = 0.85;

        public double ContextEmergencyLimitRatio { get; set; } = 0.95;

        public double ContextTokenSafetyMargin { get; set; } = 1.2;

        public AgentPermissionMode? PermissionMode { get; set; }

        public bool RequireConfirmationForScripts { get; set; } = true;

        public bool EnableToolCalling { get; set; } = true;

        public string ModelRoutingMessage { get; set; } = string.Empty;

        public string CustomSystemInstructions { get; set; } = string.Empty;

        /// <summary>
        /// 前端显式选择的 Skill 名称。这里只保存标识符，不保存路径。
        /// </summary>
        public IReadOnlyList<string> SelectedSkillNames { get; set; } = new List<string>();

        /// <summary>Plan→Agent 切换时传入的执行计划，用于进度追踪</summary>
        public ExecutionPlan ActivePlan { get; set; }

        /// <summary>当前已完成的 TodoItem 索引</summary>
        public int CurrentTodoIndex { get; set; } = 0;

        /// <summary>
        /// 当前请求若已由前端显式选择 Todo Board 的继续/重建/丢弃策略，则跳过启动前的等待面板，直接按该策略准备任务板。
        /// </summary>
        public TodoBoardRecoveryDecision? StartupTodoBoardDecision { get; set; }
    }
}
