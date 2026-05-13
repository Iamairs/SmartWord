using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Telemetry;

namespace SmartWord.Infrastructure.Telemetry
{
    /// <summary>
    /// 将同一评测事件写入多个输出端。
    /// </summary>
    public sealed class CompositeTelemetrySink : IAgentTelemetrySink
    {
        private readonly IReadOnlyList<IAgentTelemetrySink> _sinks;

        public CompositeTelemetrySink(params IAgentTelemetrySink[] sinks)
        {
            _sinks = sinks ?? Array.Empty<IAgentTelemetrySink>();
        }

        public async Task RecordAsync(AgentTelemetryEvent telemetryEvent, CancellationToken cancellationToken)
        {
            foreach (var sink in _sinks)
            {
                if (sink == null)
                {
                    continue;
                }

                await sink.RecordAsync(telemetryEvent, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
