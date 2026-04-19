using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.OfficeIntegration.Tools
{
    /// <summary>
    /// Plan 模式采访工具：由编排器拦截处理，此 ExecuteAsync 不会被实际调用。
    /// </summary>
    public sealed class AskUserQuestionTool : ITool
    {
        private static readonly JsonElement _inputSchema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{" +
            "\"question\":{\"type\":\"string\",\"description\":\"向用户提出的澄清问题\"}," +
            "\"options\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"2-3个预设选项，用户也可自由输入\"}" +
            "},\"required\":[\"question\",\"options\"]}")
            .RootElement.Clone();

        public string Name => "ask_user_question";
        public string Description => "向用户提问以澄清模糊需求，提供选项供选择。仅在 Plan 模式采访阶段使用，每轮至多3个问题。";
        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;
        public bool IsVisibleToModel => true;
        public JsonElement InputSchema => _inputSchema;

        public Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
            => Task.FromResult(ToolCallResult.Error(Name, "ask_user_question 必须通过编排器问答通道处理。"));
    }
}
