using System;
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
    /// 读取当前文档 Todo Board 的只读工具。
    /// </summary>
    public sealed class TodoReadTool : ITool
    {
        private readonly TodoManager _todoManager;

        public TodoReadTool(TodoManager todoManager)
        {
            _todoManager = todoManager ?? throw new System.ArgumentNullException(nameof(todoManager));
        }

        public string Name => "todo_read";

        public string Description => "读取当前文档的 Todo Board，返回完整任务列表、当前激活任务、统计信息和 Markdown 视图。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public bool IsVisibleToModel => true;

        public System.Text.Json.JsonElement InputSchema => System.Text.Json.JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}").RootElement.Clone();

        public async Task<ToolCallResult> ExecuteAsync(
            System.Text.Json.JsonElement input,
            IUndoScope undoScope,
            CancellationToken cancellationToken)
        {
            _ = input;
            _ = undoScope;

            var board = await _todoManager
                .EnsureBoardAsync(_todoManager.GetCurrentDocumentPathOrDefault(), cancellationToken)
                .ConfigureAwait(false);
            var stats = _todoManager.BuildStats(board);
            var boardJson = _todoManager.SerializeBoard(board);

            var payload = JsonConvert.SerializeObject(new
            {
                success = true,
                operation = "todo_read",
                message = "已读取当前 Todo Board。",
                currentTodoId = stats.CurrentTodoId,
                stats = stats,
                board = JObject.Parse(boardJson),
                markdownView = _todoManager.BuildMarkdownView(board)
            });

            return ToolCallResult.Ok(
                payload,
                metadata: new TodoToolMetadata
                {
                    IsWriteOperation = false,
                    Operation = "todo_read",
                    BoardJson = boardJson,
                    CurrentTodoId = stats.CurrentTodoId,
                    CompletedSteps = stats.HandledCount,
                    TotalSteps = stats.TotalCount
                },
                operationDescription: "读取当前 Todo Board。");
        }
    }
}
