using System.Threading;
using System.Threading.Tasks;

namespace SmartWord.Core.Telemetry
{
    /// <summary>
    /// 接收 Agent 运行事实事件的抽象输出端。
    /// </summary>
    public interface IAgentTelemetrySink
    {
        Task RecordAsync(AgentTelemetryEvent telemetryEvent, CancellationToken cancellationToken);
    }
}
