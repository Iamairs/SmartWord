using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SmartWord.Core.Telemetry;

namespace SmartWord.Infrastructure.Telemetry
{
    /// <summary>
    /// 将评测事件按 JSONL 追加到 trace 文件。
    /// </summary>
    public sealed class JsonlAgentTelemetrySink : IAgentTelemetrySink
    {
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly string _tracePath;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        public JsonlAgentTelemetrySink(string tracePath)
        {
            if (string.IsNullOrWhiteSpace(tracePath))
            {
                throw new ArgumentException("trace.jsonl 路径不能为空。", nameof(tracePath));
            }

            _tracePath = tracePath;
            var directory = Path.GetDirectoryName(_tracePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        public async Task RecordAsync(AgentTelemetryEvent telemetryEvent, CancellationToken cancellationToken)
        {
            if (telemetryEvent == null)
            {
                return;
            }

            var line = JsonConvert.SerializeObject(telemetryEvent, SerializerSettings) + Environment.NewLine;
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (var stream = new FileStream(_tracePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    await writer.WriteAsync(line).ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
