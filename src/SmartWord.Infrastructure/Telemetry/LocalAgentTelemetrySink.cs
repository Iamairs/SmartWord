using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Telemetry;
using SmartWord.Infrastructure.Persistence;

namespace SmartWord.Infrastructure.Telemetry
{
    /// <summary>
    /// 默认本地遥测 Sink。写入前将文档路径、原始参数和模型正文转换为摘要，避免明文落盘。
    /// </summary>
    public sealed class LocalAgentTelemetrySink : IAgentTelemetrySink
    {
        private static readonly HashSet<string> SummaryOnlyKeys = new HashSet<string>(
            new[]
            {
                "rawInput",
                "inputDocx",
                "assistantContent",
                "checksJson",
                "failureReason",
                "documentPath",
                "userGoal"
            },
            StringComparer.OrdinalIgnoreCase);

        private readonly JsonlAgentTelemetrySink _inner;

        public LocalAgentTelemetrySink(string tracePath)
        {
            _inner = new JsonlAgentTelemetrySink(tracePath);
        }

        public Task RecordAsync(AgentTelemetryEvent telemetryEvent, CancellationToken cancellationToken)
        {
            if (telemetryEvent == null)
            {
                return Task.CompletedTask;
            }

            return _inner.RecordAsync(CloneAndSanitize(telemetryEvent), cancellationToken);
        }

        private static AgentTelemetryEvent CloneAndSanitize(AgentTelemetryEvent source)
        {
            return new AgentTelemetryEvent
            {
                EventId = source.EventId,
                EventType = source.EventType,
                EvalRunId = source.EvalRunId,
                TaskRunId = source.TaskRunId,
                CaseId = source.CaseId,
                Level = source.Level,
                Variant = source.Variant,
                Mode = source.Mode,
                PermissionMode = source.PermissionMode,
                Model = Truncate(SecretRedactor.Redact(source.Model), 160),
                TimestampUtc = source.TimestampUtc,
                Data = SanitizeDictionary(source.Data)
            };
        }

        private static Dictionary<string, object> SanitizeDictionary(IDictionary<string, object> source)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source ?? new Dictionary<string, object>())
            {
                result[pair.Key] = SummaryOnlyKeys.Contains(pair.Key)
                    ? BuildSummary(pair.Value)
                    : SanitizeValue(pair.Value, 0);
            }

            return result;
        }

        private static object SanitizeValue(object value, int depth)
        {
            if (value == null || depth > 4)
            {
                return null;
            }

            if (value is string text)
            {
                return Truncate(SecretRedactor.Redact(text), 512);
            }

            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key) ?? string.Empty;
                    result[key] = SummaryOnlyKeys.Contains(key)
                        ? BuildSummary(entry.Value)
                        : SanitizeValue(entry.Value, depth + 1);
                }

                return result;
            }

            if (value is IEnumerable enumerable && !(value is byte[]))
            {
                return enumerable.Cast<object>()
                    .Take(100)
                    .Select(item => SanitizeValue(item, depth + 1))
                    .ToArray();
            }

            if (value.GetType().IsPrimitive || value is decimal || value is DateTime || value is DateTimeOffset)
            {
                return value;
            }

            return Truncate(SecretRedactor.Redact(value.ToString()), 512);
        }

        private static object BuildSummary(object value)
        {
            var text = SecretRedactor.Redact(Convert.ToString(value) ?? string.Empty);
            return new
            {
                length = text.Length,
                sha256 = ComputeSha256(text)
            };
        }

        private static string ComputeSha256(string value)
        {
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            var text = value ?? string.Empty;
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }
    }
}
