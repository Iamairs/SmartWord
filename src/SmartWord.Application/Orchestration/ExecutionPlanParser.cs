using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SmartWord.Core.Models;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// 从 LLM 输出的 ```json 代码块中解析 ExecutionPlan。
    /// </summary>
    public static class ExecutionPlanParser
    {
        private static readonly Regex JsonBlockRegex =
            new Regex(@"```json\s*(\{[\s\S]*?\})\s*```", RegexOptions.Compiled);

        public static bool TryParse(string content, out ExecutionPlan plan)
        {
            plan = null;
            if (string.IsNullOrWhiteSpace(content)) return false;

            var match = JsonBlockRegex.Match(content);
            if (!match.Success) return false;

            try
            {
                var obj = JObject.Parse(match.Groups[1].Value);
                var todoList = new List<TodoItem>();
                foreach (var item in obj["todo_list"] ?? new JArray())
                    todoList.Add(new TodoItem { Description = item.Value<string>() ?? string.Empty });

                var riskNotes = new List<string>();
                foreach (var item in obj["risk_notes"] ?? new JArray())
                    riskNotes.Add(item.Value<string>() ?? string.Empty);

                plan = new ExecutionPlan
                {
                    TaskDescription = obj.Value<string>("task_description") ?? string.Empty,
                    TodoList = todoList,
                    RiskNotes = riskNotes
                };
                return todoList.Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
