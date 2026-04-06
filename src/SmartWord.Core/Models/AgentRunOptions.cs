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
    }
}
