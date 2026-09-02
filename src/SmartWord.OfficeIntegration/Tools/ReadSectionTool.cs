using System;
using System.Collections.Generic;
using System.Text.Json;
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
        private static readonly JsonSerializerOptions JsonOptions = ToolJsonOptions.Default;

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;
        private readonly ReadScopeResolver _readScopeResolver;

        public ReadSectionTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _readScopeResolver = new ReadScopeResolver();
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"heading\":{\"type\":\"string\",\"minLength\":1,\"description\":\"按标题读取。与 from_para/to_para、around_cursor 互斥；标题不存在时返回诊断，不要把模糊标题当作精确范围。\"},\"include_subsections\":{\"type\":\"boolean\",\"description\":\"仅在按标题读取时使用；true 表示包含子标题内容。\"},\"from_para\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"按段落范围读取的起始段落，0-based。必须与 to_para 成对使用。\"},\"to_para\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"按段落范围读取的结束段落，0-based 且包含该段；必须不小于 from_para。\"},\"around_cursor\":{\"type\":\"boolean\",\"description\":\"true 表示只读取光标附近；与 heading/from_para/to_para 互斥。\"},\"context_window\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"description\":\"around_cursor=true 时，光标前后各读取的段落数。\"},\"max_tokens\":{\"type\":\"integer\",\"minimum\":200,\"maximum\":12000,\"description\":\"结果截断阈值。长文档应缩小范围后分段读取，而不是无限增大该值。\"}},\"oneOf\":[{\"required\":[\"heading\"]},{\"required\":[\"from_para\",\"to_para\"]},{\"required\":[\"around_cursor\"]}]}")
                .RootElement
                .Clone();
        }

        public string Name => "read_section";

        public string Description => "按标题、段落范围或光标附近读取指定片段，返回段落样式、文本与必要诊断信息。三种读取范围互斥；from_para/to_para 都是 0-based；请不要同时提供多种范围。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public bool IsVisibleToModel => true;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var contractError = ValidateInputContract(input);
            if (!string.IsNullOrWhiteSpace(contractError))
            {
                return ToolCallResult.Error(Name, contractError);
            }

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

        private static string ValidateInputContract(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object)
            {
                return "read_section 输入必须是 JSON 对象。";
            }

            var selectors = 0;
            if (input.TryGetProperty("heading", out var heading))
            {
                if (heading.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(heading.GetString()))
                {
                    return "heading 必须是非空字符串。";
                }

                selectors++;
            }

            var hasFrom = input.TryGetProperty("from_para", out var from);
            var hasTo = input.TryGetProperty("to_para", out var to);
            if (hasFrom != hasTo)
            {
                return "from_para 和 to_para 必须同时提供，且都使用 0-based 索引。";
            }

            if (hasFrom)
            {
                if (!IsNonNegativeInt(from) || !IsNonNegativeInt(to))
                {
                    return "from_para/to_para 必须是 0-based 非负整数。";
                }

                if (from.GetInt32() > to.GetInt32())
                {
                    return "to_para 必须大于或等于 from_para；请不要依赖系统自动调整范围。";
                }

                selectors++;
            }

            if (input.TryGetProperty("around_cursor", out var around))
            {
                if (around.ValueKind != JsonValueKind.True && around.ValueKind != JsonValueKind.False)
                {
                    return "around_cursor 必须是布尔值。";
                }

                if (around.GetBoolean())
                {
                    selectors++;
                }
            }

            if (selectors > 1)
            {
                return "读取范围互斥：heading、from_para/to_para、around_cursor 只能选择一种。";
            }

            if (selectors == 0)
            {
                return "必须明确指定一种读取范围：heading、from_para/to_para 或 around_cursor。不要在范围不确定时默认读取文档开头。";
            }

            if (input.TryGetProperty("context_window", out var window)
                && (!IsNonNegativeInt(window) || window.GetInt32() < 1 || window.GetInt32() > 50))
            {
                return "context_window 必须是 1 到 50 的整数。";
            }

            if (input.TryGetProperty("max_tokens", out var maxTokens)
                && (!IsNonNegativeInt(maxTokens) || maxTokens.GetInt32() < 200 || maxTokens.GetInt32() > 12000))
            {
                return "max_tokens 必须是 200 到 12000 的整数。";
            }

            return string.Empty;
        }

        private static bool IsNonNegativeInt(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out var value)
                && value >= 0;
        }
    }
}
