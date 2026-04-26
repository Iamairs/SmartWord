using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.Models;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.OfficeIntegration.Tools
{
    /// <summary>
    /// 按表格索引读取表格结构与单元格内容。
    /// </summary>
    public sealed class ReadTableTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = ToolJsonOptions.Default;

        private readonly JsonElement _inputSchema;
        private readonly WordApplicationWrapper _wordApplicationWrapper;

        public ReadTableTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"table_index\":{\"type\":\"integer\",\"description\":\"目标表格索引，0-based。第一个表格是 0，不是 1。\"},\"max_rows\":{\"type\":\"integer\",\"description\":\"最多返回多少行。\"},\"max_columns\":{\"type\":\"integer\",\"description\":\"最多返回多少列。\"}},\"required\":[\"table_index\"]}")
                .RootElement
                .Clone();
        }

        public string Name => "read_table";

        public string Description => "按 0-based 表格索引读取表格结构、锚点段落与单元格文本。table_index=0 表示第一个表格。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public bool IsVisibleToModel => true;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var tableIndex = ReadNullableInt(input, "table_index");
            if (!tableIndex.HasValue || tableIndex.Value < 0)
            {
                return ToolCallResult.Error(Name, "table_index 必须是大于等于 0 的整数。");
            }

            var maxRows = ReadNullableInt(input, "max_rows") ?? 20;
            var maxColumns = ReadNullableInt(input, "max_columns") ?? 10;
            var tableResult = await _wordApplicationWrapper
                .ReadTableAsync(tableIndex.Value, maxRows, maxColumns)
                .ConfigureAwait(false);
            if (tableResult == null || !tableResult.Success || tableResult.Snapshot == null)
            {
                return ToolCallResult.Error(
                    Name,
                    string.IsNullOrWhiteSpace(tableResult == null ? null : tableResult.FailureReason)
                        ? "指定的表格不存在，或当前文档不可读取。"
                        : tableResult.FailureReason);
            }

            var table = tableResult.Snapshot;
            var diagnostics = tableResult.Diagnostics ?? new ReadDiagnostics();
            if (table.RowsTruncated)
            {
                diagnostics.IsPartial = true;
                diagnostics.AddWarning("表格行数超过 max_rows，结果已截断。");
            }

            if (table.ColumnsTruncated)
            {
                diagnostics.IsPartial = true;
                diagnostics.AddWarning("表格列数超过 max_columns，结果已截断。");
            }

            var payload = new
            {
                table_index = table.TableIndex,
                anchor_para_index = table.AnchorParagraphIndex,
                row_count = table.RowCount,
                column_count = table.ColumnCount,
                rows = table.Rows,
                diagnostics = BuildDiagnosticsPayload(diagnostics)
            };

            return ToolCallResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
        }

        private static int? ReadNullableInt(JsonElement input, string propertyName)
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            {
                return value;
            }

            return null;
        }

        private static object BuildDiagnosticsPayload(ReadDiagnostics diagnostics)
        {
            return diagnostics == null || (!diagnostics.IsPartial && !diagnostics.HasWarnings)
                ? null
                : new
                {
                    is_partial = diagnostics.IsPartial,
                    warnings = diagnostics.Warnings
                };
        }
    }
}
