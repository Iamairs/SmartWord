using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SmartWord.Infrastructure.Telemetry
{
    /// <summary>
    /// 从本地 JSONL 观测文件读取有限窗口，并汇总 Skill 使用情况。
    /// </summary>
    public sealed class LocalSkillTelemetryReader
    {
        private const int MaximumReadBytes = 1024 * 1024;
        private const int MaximumEvents = 500;
        private readonly string _tracePath;

        public LocalSkillTelemetryReader(string tracePath)
        {
            _tracePath = tracePath ?? string.Empty;
        }

        public LocalSkillTelemetrySummary ReadSummary()
        {
            var summary = new LocalSkillTelemetrySummary
            {
                IsEnabled = true,
                StoragePath = _tracePath
            };
            if (string.IsNullOrWhiteSpace(_tracePath) || !File.Exists(_tracePath))
            {
                return summary;
            }

            var skillUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var telemetryEvent in ReadRecentEvents())
            {
                summary.EventCount++;
                var timestamp = ReadTimestamp(telemetryEvent["timestampUtc"]);
                if (timestamp.HasValue
                    && (!summary.LastEventAtUtc.HasValue || timestamp.Value > summary.LastEventAtUtc.Value))
                {
                    summary.LastEventAtUtc = timestamp;
                }

                var eventType = telemetryEvent.Value<string>("eventType") ?? string.Empty;
                if (string.Equals(eventType, "skill_context_resolved", StringComparison.OrdinalIgnoreCase))
                {
                    summary.SkillContextResolvedCount++;
                    CountActiveSkills(telemetryEvent["data"]?["activeSkillNames"], skillUsage);
                }
                else if (string.Equals(eventType, "tool_call_failed", StringComparison.OrdinalIgnoreCase))
                {
                    summary.ToolFailureCount++;
                }
                else if (string.Equals(eventType, "task_completed", StringComparison.OrdinalIgnoreCase))
                {
                    summary.CompletedTaskCount++;
                }
                else if (string.Equals(eventType, "task_failed", StringComparison.OrdinalIgnoreCase))
                {
                    summary.FailedTaskCount++;
                }
            }

            summary.TopSkills = skillUsage
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(item => new LocalSkillUsageSummary
                {
                    Name = item.Key,
                    Count = item.Value
                })
                .ToArray();
            return summary;
        }

        private IEnumerable<JObject> ReadRecentEvents()
        {
            var lines = ReadTailText()
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            return lines
                .Skip(Math.Max(0, lines.Length - MaximumEvents))
                .Select(TryParseObject)
                .Where(item => item != null);
        }

        private string ReadTailText()
        {
            using (var stream = new FileStream(_tracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var start = Math.Max(0, stream.Length - MaximumReadBytes);
                stream.Seek(start, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                {
                    var text = reader.ReadToEnd();
                    if (start <= 0)
                    {
                        return text;
                    }

                    var firstLineBreak = text.IndexOf('\n');
                    return firstLineBreak < 0 ? string.Empty : text.Substring(firstLineBreak + 1);
                }
            }
        }

        private static JObject TryParseObject(string line)
        {
            try
            {
                return JObject.Parse(line);
            }
            catch
            {
                return null;
            }
        }

        private static DateTimeOffset? ReadTimestamp(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Date)
            {
                var dateTime = token.Value<DateTime>();
                return new DateTimeOffset(dateTime.ToUniversalTime());
            }

            return DateTimeOffset.TryParse(token.Value<string>(), out var timestamp)
                ? timestamp
                : (DateTimeOffset?)null;
        }

        private static void CountActiveSkills(JToken token, IDictionary<string, int> counts)
        {
            if (!(token is JArray names))
            {
                return;
            }

            foreach (var nameToken in names)
            {
                var name = (nameToken.Value<string>() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                counts[name] = counts.TryGetValue(name, out var current) ? current + 1 : 1;
            }
        }
    }

    /// <summary>
    /// 面向前端的本地 Skill 观测摘要，不包含文档正文或原始工具参数。
    /// </summary>
    public sealed class LocalSkillTelemetrySummary
    {
        public bool IsEnabled { get; set; }

        public string StoragePath { get; set; } = string.Empty;

        public int EventCount { get; set; }

        public int SkillContextResolvedCount { get; set; }

        public int CompletedTaskCount { get; set; }

        public int FailedTaskCount { get; set; }

        public int ToolFailureCount { get; set; }

        public DateTimeOffset? LastEventAtUtc { get; set; }

        public IReadOnlyList<LocalSkillUsageSummary> TopSkills { get; set; } = Array.Empty<LocalSkillUsageSummary>();
    }

    /// <summary>
    /// 表示最近观测窗口内单个 Skill 的激活次数。
    /// </summary>
    public sealed class LocalSkillUsageSummary
    {
        public string Name { get; set; } = string.Empty;

        public int Count { get; set; }
    }
}
