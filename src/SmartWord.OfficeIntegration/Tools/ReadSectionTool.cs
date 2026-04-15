using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.Models;
using SmartWord.OfficeIntegration.Reading;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.OfficeIntegration.Tools
{
    /// <summary>
    /// 按标题、段落范围或光标附近读取文档内容。
    /// </summary>
    public sealed class ReadSectionTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;
        private readonly ReadScopeResolver _readScopeResolver;

        public ReadSectionTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _readScopeResolver = new ReadScopeResolver();
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"heading\":{\"type\":\"string\",\"description\":\"按标题读取时使用的标题文本。\"},\"include_subsections\":{\"type\":\"boolean\",\"description\":\"仅在按标题读取时使用；true 表示包含子标题内容。\"},\"from_para\":{\"type\":\"integer\",\"description\":\"起始段落，0-based。仅在按段落范围读取时使用。\"},\"to_para\":{\"type\":\"integer\",\"description\":\"结束段落，0-based 且包含该段。仅在按段落范围读取时使用。\"},\"around_cursor\":{\"type\":\"boolean\",\"description\":\"true 表示忽略 heading/from_para/to_para，改为读取光标附近段落。\"},\"context_window\":{\"type\":\"integer\",\"description\":\"around_cursor=true 时，表示光标前后各读取多少段。\"},\"max_tokens\":{\"type\":\"integer\",\"description\":\"结果截断阈值，按粗略 token 估算。\"}}}")
                .RootElement
                .Clone();
        }

        public string Name => "read_section";

        public string Description => "按标题、段落范围或光标附近读取指定片段，返回段落样式、文本与必要诊断信息。from_para/to_para 都是 0-based；若 around_cursor=true，则优先按光标附近读取。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var heading = ReadString(input, "heading");
            var includeSubsections = ReadBool(input, "include_subsections", true);
            var fromPara = ReadNullableInt(input, "from_para");
            var toPara = ReadNullableInt(input, "to_para");
            var aroundCursor = ReadBool(input, "around_cursor", false);
            var contextWindow = Math.Max(1, ReadNullableInt(input, "context_window") ?? 5);
            var maxTokens = Math.Max(200, ReadNullableInt(input, "max_tokens") ?? 2000);

            var snapshotBuilder = new ReadOnlyDocumentSnapshotBuilder(_wordApplicationWrapper);
            var snapshot = await snapshotBuilder.BuildAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.ParagraphCount <= 0)
            {
                return ToolCallResult.Ok(JsonSerializer.Serialize(new
                {
                    range = new { from = 0, to = 0, heading = string.Empty },
                    paragraphs = Array.Empty<object>(),
                    truncated = false,
                    token_estimate = 0
                }, JsonOptions));
            }

            var diagnostics = new ReadDiagnostics();
            var resolvedRange = _readScopeResolver.Resolve(
                new ReadScope
                {
                    Heading = heading,
                    IncludeSubsections = includeSubsections,
                    FromParagraph = fromPara,
                    ToParagraph = toPara,
                    AroundCursor = aroundCursor,
                    ContextWindow = contextWindow
                },
                snapshot.ParagraphCount,
                snapshot.Headings,
                snapshot.CursorParagraphIndex,
                snapshot.Selection,
                diagnostics);

            var paragraphSnapshots = await snapshotBuilder
                .ReadParagraphsAsync(resolvedRange.FromParagraph, resolvedRange.ToParagraph, cancellationToken)
                .ConfigureAwait(false);

            var emittedParagraphs = new List<object>();
            var tokenEstimate = 0;
            var truncated = false;
            foreach (var paragraph in paragraphSnapshots)
            {
                var paragraphTokenEstimate = Math.Max(1, (paragraph.Text ?? string.Empty).Length / 2);
                if (tokenEstimate + paragraphTokenEstimate > maxTokens && emittedParagraphs.Count > 0)
                {
                    truncated = true;
                    diagnostics.IsPartial = true;
                    diagnostics.AddWarning("结果因 max_tokens 限制被截断。");
                    break;
                }

                tokenEstimate += paragraphTokenEstimate;
                emittedParagraphs.Add(new
                {
                    index = paragraph.Index,
                    style = paragraph.Style,
                    text = paragraph.Text
                });
            }

            var payload = new
            {
                range = new
                {
                    from = resolvedRange.FromParagraph,
                    to = resolvedRange.ToParagraph,
                    heading = resolvedRange.HeadingText
                },
                paragraphs = emittedParagraphs,
                truncated,
                token_estimate = tokenEstimate,
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

        private static bool ReadBool(JsonElement input, string propertyName, bool defaultValue)
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(propertyName, out var property))
            {
                return defaultValue;
            }

            return property.ValueKind == JsonValueKind.True
                || (property.ValueKind != JsonValueKind.False && defaultValue);
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
