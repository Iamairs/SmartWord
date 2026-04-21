using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SmartWord.Core.Models;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// 从 LLM 输出中尽量提取可执行的计划结构，兼容 JSON 代码块、裸 JSON 和常见 Markdown 计划格式。
    /// </summary>
    public static class ExecutionPlanParser
    {
        private static readonly Regex FencedBlockRegex =
            new Regex(@"```(?:\w+)?\s*([\s\S]*?)```", RegexOptions.Compiled);

        private static readonly Regex TodoListItemRegex =
            new Regex(@"^\s*(?:[-*+]|(?:\d+)[\.\)]|(?:\[[ xX]\]))\s*(.+?)\s*$", RegexOptions.Compiled);

        public static bool TryParse(string content, out ExecutionPlan plan)
        {
            plan = null;
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            foreach (var candidate in EnumerateJsonCandidates(content))
            {
                if (TryParseJsonCandidate(candidate, out plan))
                {
                    return true;
                }
            }

            return TryParseMarkdownPlan(content, out plan);
        }

        private static IEnumerable<string> EnumerateJsonCandidates(string content)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in FencedBlockRegex.Matches(content))
            {
                if (!match.Success)
                {
                    continue;
                }

                var blockContent = (match.Groups[1].Value ?? string.Empty).Trim();
                if (TryAddCandidate(blockContent, seen, out var candidate))
                {
                    yield return candidate;
                }

                var jsonInBlock = ExtractFirstJsonObject(blockContent);
                if (TryAddCandidate(jsonInBlock, seen, out candidate))
                {
                    yield return candidate;
                }
            }

            var jsonInContent = ExtractFirstJsonObject(content);
            if (TryAddCandidate(jsonInContent, seen, out var rootCandidate))
            {
                yield return rootCandidate;
            }
        }

        private static bool TryAddCandidate(string candidate, ISet<string> seen, out string normalizedCandidate)
        {
            normalizedCandidate = string.IsNullOrWhiteSpace(candidate)
                ? string.Empty
                : candidate.Trim();
            if (string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                return false;
            }

            if (!normalizedCandidate.StartsWith("{", StringComparison.Ordinal))
            {
                return false;
            }

            return seen.Add(normalizedCandidate);
        }

        private static bool TryParseJsonCandidate(string candidate, out ExecutionPlan plan)
        {
            plan = null;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            try
            {
                var obj = JObject.Parse(candidate);
                return TryBuildPlan(obj, out plan);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBuildPlan(JObject obj, out ExecutionPlan plan)
        {
            plan = null;
            if (obj == null)
            {
                return false;
            }

            var todoList = ParseTodoList(
                obj["todo_list"]
                ?? obj["todoList"]
                ?? obj["todos"]
                ?? obj["steps"]);

            if (todoList.Count == 0)
            {
                return false;
            }

            var riskNotes = ParseStringList(
                obj["risk_notes"]
                ?? obj["riskNotes"]
                ?? obj["risks"]);

            plan = new ExecutionPlan
            {
                TaskDescription = FirstNonEmptyString(
                    obj,
                    "task_description",
                    "taskDescription",
                    "task",
                    "description",
                    "summary") ?? string.Empty,
                TodoList = todoList,
                RiskNotes = riskNotes
            };

            return true;
        }

        private static List<TodoItem> ParseTodoList(JToken token)
        {
            var todoList = new List<TodoItem>();
            if (token == null || token.Type == JTokenType.Null)
            {
                return todoList;
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Children())
                {
                    var todoItem = TryParseTodoItem(item);
                    if (todoItem != null)
                    {
                        todoList.Add(todoItem);
                    }
                }

                return todoList;
            }

            var singleItem = TryParseTodoItem(token);
            if (singleItem != null)
            {
                todoList.Add(singleItem);
            }

            return todoList;
        }

        private static TodoItem TryParseTodoItem(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                var description = (token.Value<string>() ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(description)
                    ? null
                    : new TodoItem { Description = description };
            }

            if (token.Type != JTokenType.Object)
            {
                return null;
            }

            var obj = (JObject)token;
            var objectDescription = FirstNonEmptyString(
                obj,
                "description",
                "content",
                "text",
                "title",
                "task",
                "name");

            if (string.IsNullOrWhiteSpace(objectDescription))
            {
                return null;
            }

            return new TodoItem
            {
                Description = objectDescription.Trim(),
                Status = ParseTodoStatus(obj["status"] ?? obj["state"])
            };
        }

        private static TodoItemStatus ParseTodoStatus(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return TodoItemStatus.Pending;
            }

            if (token.Type == JTokenType.Integer)
            {
                var value = token.Value<int>();
                if (Enum.IsDefined(typeof(TodoItemStatus), value))
                {
                    return (TodoItemStatus)value;
                }

                return TodoItemStatus.Pending;
            }

            var raw = (token.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            switch (raw)
            {
                case "in_progress":
                case "inprogress":
                case "doing":
                case "running":
                    return TodoItemStatus.InProgress;
                case "completed":
                case "complete":
                case "done":
                case "finished":
                    return TodoItemStatus.Completed;
                case "failed":
                case "error":
                    return TodoItemStatus.Failed;
                case "skipped":
                case "skip":
                    return TodoItemStatus.Skipped;
                default:
                    return TodoItemStatus.Pending;
            }
        }

        private static List<string> ParseStringList(JToken token)
        {
            var results = new List<string>();
            if (token == null || token.Type == JTokenType.Null)
            {
                return results;
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Children())
                {
                    var text = ExtractListText(item);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        results.Add(text);
                    }
                }

                return results;
            }

            var single = ExtractListText(token);
            if (!string.IsNullOrWhiteSpace(single))
            {
                results.Add(single);
            }

            return results;
        }

        private static string ExtractListText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return string.Empty;
            }

            if (token.Type == JTokenType.String)
            {
                return (token.Value<string>() ?? string.Empty).Trim();
            }

            if (token.Type == JTokenType.Object)
            {
                return FirstNonEmptyString((JObject)token, "description", "content", "text", "title", "name") ?? string.Empty;
            }

            return token.ToString().Trim();
        }

        private static string FirstNonEmptyString(JObject obj, params string[] propertyNames)
        {
            if (obj == null || propertyNames == null)
            {
                return string.Empty;
            }

            foreach (var propertyName in propertyNames)
            {
                var value = obj.Value<string>(propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static string ExtractFirstJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var startIndex = text.IndexOf('{');
            if (startIndex < 0)
            {
                return string.Empty;
            }

            var depth = 0;
            var inString = false;
            var isEscaped = false;
            for (var index = startIndex; index < text.Length; index++)
            {
                var current = text[index];
                if (inString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        isEscaped = true;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                    continue;
                }

                if (current != '}')
                {
                    continue;
                }

                depth--;
                if (depth == 0)
                {
                    return text.Substring(startIndex, index - startIndex + 1);
                }
            }

            return string.Empty;
        }

        private static bool TryParseMarkdownPlan(string content, out ExecutionPlan plan)
        {
            plan = null;
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var taskBuilder = new StringBuilder();
            var todoItems = new List<TodoItem>();
            var riskNotes = new List<string>();
            var currentSection = MarkdownSection.None;

            var lines = content.Replace("\r\n", "\n").Split('\n');
            foreach (var rawLine in lines)
            {
                var line = (rawLine ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var nextSection = DetectSection(line, out var inlineContent);
                if (nextSection != MarkdownSection.None)
                {
                    currentSection = nextSection;
                    AppendSectionContent(currentSection, inlineContent, taskBuilder, todoItems, riskNotes);
                    continue;
                }

                AppendSectionContent(currentSection, line, taskBuilder, todoItems, riskNotes);
            }

            if (todoItems.Count == 0)
            {
                return false;
            }

            plan = new ExecutionPlan
            {
                TaskDescription = taskBuilder.ToString().Trim(),
                TodoList = todoItems,
                RiskNotes = riskNotes
            };

            return true;
        }

        private static MarkdownSection DetectSection(string line, out string inlineContent)
        {
            inlineContent = string.Empty;
            var normalized = line.Trim().TrimStart('#').Trim();
            var colonIndex = normalized.IndexOf(':');
            if (colonIndex < 0)
            {
                colonIndex = normalized.IndexOf('：');
            }

            var heading = colonIndex >= 0
                ? normalized.Substring(0, colonIndex).Trim()
                : normalized;
            inlineContent = colonIndex >= 0 && colonIndex < normalized.Length - 1
                ? normalized.Substring(colonIndex + 1).Trim()
                : string.Empty;

            var sectionKey = heading.Replace(" ", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            if (sectionKey.Contains("taskdescription")
                || sectionKey.Contains("task")
                || sectionKey.Contains("description")
                || sectionKey.Contains("任务说明")
                || sectionKey.Contains("任务理解"))
            {
                return MarkdownSection.Task;
            }

            if (sectionKey.Contains("todolist")
                || sectionKey.Equals("todo")
                || sectionKey.Contains("steps")
                || sectionKey.Contains("待办")
                || sectionKey.Contains("执行步骤")
                || sectionKey.Contains("待办清单"))
            {
                return MarkdownSection.Todo;
            }

            if (sectionKey.Contains("risknotes")
                || sectionKey.Equals("risk")
                || sectionKey.Equals("risks")
                || sectionKey.Contains("风险")
                || sectionKey.Contains("注意事项"))
            {
                return MarkdownSection.Risk;
            }

            return MarkdownSection.None;
        }

        private static void AppendSectionContent(
            MarkdownSection section,
            string content,
            StringBuilder taskBuilder,
            IList<TodoItem> todoItems,
            IList<string> riskNotes)
        {
            if (string.IsNullOrWhiteSpace(content) || section == MarkdownSection.None)
            {
                return;
            }

            var normalizedContent = content.Trim();
            switch (section)
            {
                case MarkdownSection.Task:
                    if (taskBuilder.Length > 0)
                    {
                        taskBuilder.Append(' ');
                    }

                    taskBuilder.Append(StripListPrefix(normalizedContent));
                    break;
                case MarkdownSection.Todo:
                    AddTodoMarkdownEntry(normalizedContent, todoItems);
                    break;
                case MarkdownSection.Risk:
                    AddRiskMarkdownEntry(normalizedContent, riskNotes);
                    break;
            }
        }

        private static void AddTodoMarkdownEntry(string content, IList<TodoItem> todoItems)
        {
            var itemText = StripListPrefix(content);
            if (string.IsNullOrWhiteSpace(itemText))
            {
                return;
            }

            todoItems.Add(new TodoItem
            {
                Description = itemText
            });
        }

        private static void AddRiskMarkdownEntry(string content, IList<string> riskNotes)
        {
            var itemText = StripListPrefix(content);
            if (string.IsNullOrWhiteSpace(itemText))
            {
                return;
            }

            riskNotes.Add(itemText);
        }

        private static string StripListPrefix(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            var match = TodoListItemRegex.Match(content);
            return match.Success
                ? (match.Groups[1].Value ?? string.Empty).Trim()
                : content.Trim();
        }

        private enum MarkdownSection
        {
            None = 0,
            Task = 1,
            Todo = 2,
            Risk = 3
        }
    }
}
