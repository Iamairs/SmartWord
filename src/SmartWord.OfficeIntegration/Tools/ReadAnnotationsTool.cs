using System.Collections.Generic;
using System.Linq;
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
    /// 读取文档批注内容与锚点信息。
    /// </summary>
    public sealed class ReadAnnotationsTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = ToolJsonOptions.Default;

        private readonly JsonElement _inputSchema;
        private readonly WordApplicationWrapper _wordApplicationWrapper;

        public ReadAnnotationsTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"author\":{\"type\":\"string\",\"description\":\"按作者名过滤批注；为空表示不过滤。\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100,\"description\":\"最多返回多少条批注，超出时返回 diagnostics。返回结果中的 para_index 一律使用 0-based。\"}}}")
                .RootElement
                .Clone();
        }

        public string Name => "read_annotations";

        public string Description => "读取文档批注，返回作者、时间、批注内容、锚点文本和所在段落。结果中的 para_index 一律使用 0-based。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public bool IsVisibleToModel => true;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var author = ReadString(input, "author");
            var maxResults = ReadNullableInt(input, "max_results") ?? 20;
            if (maxResults < 1 || maxResults > 100)
            {
                return ToolCallResult.Error(Name, "max_results 必须是 1 到 100 的整数。请使用 author 过滤或分批读取，不要请求过大的批注结果。" );
            }
            var annotations = await _wordApplicationWrapper
                .ReadAnnotationsAsync(author, maxResults)
                .ConfigureAwait(false);

            var diagnostics = new ReadDiagnostics();
            if (annotations.Count >= maxResults)
            {
                diagnostics.IsPartial = true;
                diagnostics.AddWarning("批注数量超过 max_results，结果可能已截断。");
            }

            var payload = new
            {
                author = author,
                total_annotations = annotations.Count,
                results = annotations.Select(item => new
                {
                    index = item.AnnotationIndex,
                    author = item.Author,
                    created_at = item.CreatedAt,
                    text = item.Text,
                    anchor_text = item.AnchorText,
                    para_index = item.ParagraphIndex
                }),
                diagnostics = BuildDiagnosticsPayload(diagnostics)
            };

            return ToolCallResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
        }

        private static string ReadString(JsonElement input, string propertyName)
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(propertyName, out var property))
            {
                return string.Empty;
            }

            return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : string.Empty;
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
