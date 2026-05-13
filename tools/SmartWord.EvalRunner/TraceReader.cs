using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace SmartWord.EvalRunner
{
    internal static class TraceReader
    {
        public static IReadOnlyList<JObject> Read(string traceJsonl, string caseId)
        {
            var items = new List<JObject>();
            if (!File.Exists(traceJsonl))
            {
                return items;
            }

            foreach (var line in File.ReadLines(traceJsonl))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var item = JObject.Parse(line);
                    if (string.IsNullOrWhiteSpace(caseId)
                        || item.Value<string>("caseId") == caseId)
                    {
                        items.Add(item);
                    }
                }
                catch
                {
                    // 忽略损坏行，原始 trace 仍保留供人工排查。
                }
            }

            return items;
        }
    }
}
