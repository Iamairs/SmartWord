using System;
using System.Threading;

namespace SmartWord.Core.Telemetry
{
    /// <summary>
    /// 在当前异步调用链上传递 Benchmark 运行上下文。
    /// </summary>
    public sealed class AgentTelemetryScope : IDisposable
    {
        private static readonly AsyncLocal<AgentTelemetryContext> CurrentContext =
            new AsyncLocal<AgentTelemetryContext>();

        private readonly AgentTelemetryContext _previous;

        public AgentTelemetryScope(AgentTelemetryContext context)
        {
            _previous = CurrentContext.Value;
            CurrentContext.Value = context;
        }

        public static AgentTelemetryContext Current
        {
            get { return CurrentContext.Value; }
        }

        public void Dispose()
        {
            CurrentContext.Value = _previous;
        }
    }
}
