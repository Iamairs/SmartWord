using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SmartWord.EvalRunner
{
    internal abstract class CheckScorerBase : ICheckScorer
    {
        private readonly HashSet<string> _types;

        protected CheckScorerBase(params string[] types)
        {
            _types = new HashSet<string>(types ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyCollection<string> Types => _types;

        public bool CanScore(string type)
        {
            return _types.Contains(type ?? string.Empty);
        }

        public abstract CheckResult Score(ScoreContext context);

        protected static double Points(JObject check)
        {
            return check.Value<double?>("points") ?? 0;
        }

        protected static CheckResult Result(
            ScoreContext context,
            string category,
            bool passed,
            string reason,
            string expected = "",
            string actual = "",
            bool safetyViolation = false)
        {
            var type = context.Check.Value<string>("type") ?? string.Empty;
            return CheckResult.Deterministic(
                type,
                category,
                Points(context.Check),
                passed,
                reason,
                expected,
                actual,
                safetyViolation);
        }

        protected static IReadOnlyList<string> ReadStrings(JToken token)
        {
            if (token is JArray array)
            {
                return array.Select(item => item.Value<string>() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList();
            }

            var value = token?.Value<string>() ?? string.Empty;
            return string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value };
        }

        protected static int CountOccurrences(string text, string value)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            {
                return 0;
            }

            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        protected static bool EventIs(JObject item, string eventType)
        {
            return string.Equals(item?.Value<string>("eventType"), eventType, StringComparison.OrdinalIgnoreCase);
        }

        protected static bool WasToolCalled(IReadOnlyList<JObject> trace, string toolName)
        {
            return trace.Any(item =>
            {
                var eventType = item.Value<string>("eventType") ?? string.Empty;
                return eventType.StartsWith("tool_call_", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item["data"]?["toolName"]?.Value<string>(), toolName, StringComparison.OrdinalIgnoreCase);
            });
        }

        protected static string AssistantText(IReadOnlyList<JObject> trace)
        {
            var text = string.Join("\n", trace
                .Where(item => EventIs(item, "llm_call_completed"))
                .Select(item => item["data"]?["assistantContent"]?.Value<string>() ?? string.Empty));
            return string.IsNullOrWhiteSpace(text)
                ? string.Join("\n", trace.Select(item => item.ToString(Formatting.None)))
                : text;
        }
    }
}
