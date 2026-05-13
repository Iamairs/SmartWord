namespace SmartWord.Core.Telemetry
{
    /// <summary>
    /// Benchmark 单个任务运行的公共上下文字段。
    /// </summary>
    public sealed class AgentTelemetryContext
    {
        public string EvalRunId { get; set; } = string.Empty;

        public string TaskRunId { get; set; } = string.Empty;

        public string CaseId { get; set; } = string.Empty;

        public string Level { get; set; } = string.Empty;

        public string Variant { get; set; } = string.Empty;
    }
}
