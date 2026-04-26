using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartWord.Application.Todo;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Tools
{
    /// <summary>
    /// 以受限动作更新当前文档 Todo Board 的写工具。
    /// </summary>
    public sealed class TodoWriteTool : ITool
    {
        private readonly TodoManager _todoManager;

        public TodoWriteTool(TodoManager todoManager)
        {
            _todoManager = todoManager ?? throw new ArgumentNullException(nameof(todoManager));
        }

        public string Name => "todo_write";

        public string Description => "按结构化动作更新当前文档的 Todo Board，只允许受控地新增、更新、改状态、重排或重建任务板。";

        public ToolPermission RequiredPermission => ToolPermission.StateWrite;

        public bool IsVisibleToModel => true;

        public System.Text.Json.JsonElement InputSchema => System.Text.Json.JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{" +
            "\"action\":{\"type\":\"string\"}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"content\":{\"type\":\"string\"}," +
            "\"notes\":{\"type\":\"string\"}," +
            "\"status\":{\"type\":\"string\",\"enum\":[\"pending\",\"in_progress\",\"completed\",\"failed\",\"skipped\"]}," +
            "\"order\":{\"type\":\"integer\"}," +
            "\"ordered_ids\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
            "\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"id\":{\"type\":\"string\"}," +
                "\"content\":{\"type\":\"string\"}," +
                "\"notes\":{\"type\":\"string\"}," +
                "\"status\":{\"type\":\"string\",\"enum\":[\"pending\",\"in_progress\",\"completed\",\"failed\",\"skipped\"]}," +
                "\"order\":{\"type\":\"integer\"}" +
            "},\"required\":[\"id\",\"content\"]}}" +
            "},\"required\":[\"action\"],\"additionalProperties\":false}")
            .RootElement.Clone();

        public async Task<ToolCallResult> ExecuteAsync(
            System.Text.Json.JsonElement input,
            IUndoScope undoScope,
            CancellationToken cancellationToken)
        {
            _ = undoScope;

            TodoWriteRequest request;
            try
            {
                request = ParseRequest(input.GetRawText());
            }
            catch (Exception ex)
            {
                return ToolCallResult.Error(Name, "TodoWrite 输入解析失败：" + ex.Message);
            }

            try
            {
                var result = await _todoManager
                    .ApplyChangeAsync(_todoManager.GetCurrentDocumentPathOrDefault(), request, cancellationToken)
                    .ConfigureAwait(false);
                var payload = JsonConvert.SerializeObject(new
                {
                    success = true,
                    operation = result.Operation,
                    message = result.Message,
                    updatedItemId = result.UpdatedItemId,
                    currentTodoId = result.CurrentTodoId,
                    stats = result.Stats,
                    board = string.IsNullOrWhiteSpace(result.BoardJson) ? null : JObject.Parse(result.BoardJson),
                    markdownView = result.MarkdownView
                });

                return ToolCallResult.Ok(
                    payload,
                    metadata: new TodoToolMetadata
                    {
                        IsWriteOperation = true,
                        Operation = result.Operation,
                        BoardJson = result.BoardJson,
                        CurrentTodoId = result.CurrentTodoId,
                        CompletedSteps = result.Stats.HandledCount,
                        TotalSteps = result.Stats.TotalCount
                    },
                    operationDescription: "更新当前 Todo Board。");
            }
            catch (Exception ex)
            {
                return ToolCallResult.Error(Name, ex.Message);
            }
        }

        private static TodoWriteRequest ParseRequest(string rawJson)
        {
            var obj = JObject.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
            return new TodoWriteRequest
            {
                Action = obj.Value<string>("action") ?? string.Empty,
                Id = obj.Value<string>("id") ?? string.Empty,
                Content = obj.Value<string>("content") ?? string.Empty,
                Notes = obj.Value<string>("notes") ?? string.Empty,
                Status = ParseStatus(obj.Value<string>("status")),
                Order = obj.Value<int?>("order"),
                OrderedIds = ((JArray)obj["ordered_ids"] ?? new JArray())
                    .Select(item => item.Value<string>() ?? string.Empty)
                    .ToList(),
                Items = ((JArray)obj["items"] ?? new JArray())
                    .Select((item, index) => new TodoBoardItem
                    {
                        Id = item.Value<string>("id") ?? string.Empty,
                        Content = item.Value<string>("content") ?? string.Empty,
                        Notes = item.Value<string>("notes") ?? string.Empty,
                        Status = ParseStatus(item.Value<string>("status")) ?? TodoItemStatus.Pending,
                        Order = item.Value<int?>("order") ?? (index + 1)
                    })
                    .ToList()
            };
        }

        private static TodoItemStatus? ParseStatus(string rawStatus)
        {
            if (string.IsNullOrWhiteSpace(rawStatus))
            {
                return null;
            }

            switch (rawStatus.Trim().ToLowerInvariant())
            {
                case "pending":
                    return TodoItemStatus.Pending;
                case "in_progress":
                    return TodoItemStatus.InProgress;
                case "completed":
                    return TodoItemStatus.Completed;
                case "failed":
                    return TodoItemStatus.Failed;
                case "skipped":
                    return TodoItemStatus.Skipped;
                default:
                    throw new InvalidOperationException("TodoWrite.status 非法。允许值：pending、in_progress、completed、failed、skipped。");
            }
        }
    }
}
