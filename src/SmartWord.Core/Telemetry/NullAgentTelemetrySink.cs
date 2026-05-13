using System.Threading;
using System.Threading.Tasks;

namespace SmartWord.Core.Telemetry
{
    /// <summary>
    /// 默认空实现，确保未开启评测时不影响插件主流程。
    /// </summary>
    public sealed class NullAgentTelemetrySink : IAgentTelemetrySink
    {
        public static readonly NullAgentTelemetrySink Instance = new NullAgentTelemetrySink();

        private NullAgentTelemetrySink()
        {
        }

        public Task RecordAsync(AgentTelemetryEvent telemetryEvent, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
