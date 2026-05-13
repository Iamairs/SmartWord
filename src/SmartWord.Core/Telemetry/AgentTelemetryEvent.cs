using System;
using System.Collections.Generic;

namespace SmartWord.Core.Telemetry
{
    /// <summary>
    /// 评测运行时的事实事件。Runtime 只写事实，离线 Scorer 再判分。
    /// </summary>
    public sealed class AgentTelemetryEvent
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString("N");

        public string EventType { get; set; } = string.Empty;

        public string EvalRunId { get; set; } = string.Empty;

        public string TaskRunId { get; set; } = string.Empty;

        public string CaseId { get; set; } = string.Empty;

        public string Level { get; set; } = string.Empty;

        public string Variant { get; set; } = string.Empty;

        public string Mode { get; set; } = string.Empty;

        public string PermissionMode { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();

        public static AgentTelemetryEvent Create(string eventType)
        {
            return new AgentTelemetryEvent
            {
                EventType = eventType ?? string.Empty,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
    }
}
