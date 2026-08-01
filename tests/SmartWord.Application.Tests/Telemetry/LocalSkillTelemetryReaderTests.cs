using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Telemetry;
using SmartWord.Infrastructure.Telemetry;
using Xunit;

namespace SmartWord.Application.Tests.Telemetry
{
    public sealed class LocalSkillTelemetryReaderTests
    {
        [Fact]
        public async Task ReadSummary_RecentJsonl_ReportsSkillAndTaskCounts()
        {
            var root = Path.Combine(Path.GetTempPath(), "smartword-telemetry-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "agent-events.jsonl");
            try
            {
                var sink = new LocalAgentTelemetrySink(path);
                await sink.RecordAsync(CreateEvent("skill_context_resolved", new Dictionary<string, object>
                {
                    ["activeSkillNames"] = new[] { "doc-review", "doc-review" }
                }), CancellationToken.None);
                await sink.RecordAsync(CreateEvent("task_completed", null), CancellationToken.None);
                await sink.RecordAsync(CreateEvent("tool_call_failed", null), CancellationToken.None);

                var summary = new LocalSkillTelemetryReader(path).ReadSummary();

                Assert.True(summary.IsEnabled);
                Assert.Equal(3, summary.EventCount);
                Assert.Equal(1, summary.SkillContextResolvedCount);
                Assert.Equal(1, summary.CompletedTaskCount);
                Assert.Equal(1, summary.ToolFailureCount);
                Assert.Contains(summary.TopSkills, item => item.Name == "doc-review" && item.Count == 2);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static AgentTelemetryEvent CreateEvent(string type, IDictionary<string, object> data)
        {
            return new AgentTelemetryEvent
            {
                EventType = type,
                TimestampUtc = DateTimeOffset.UtcNow,
                Data = data == null
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object>(data)
            };
        }
    }
}
