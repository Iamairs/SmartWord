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
    /// 在文档段落中执行关键词或正则搜索。
    /// </summary>
    public sealed class GrepDocumentTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = ToolJsonOptions.Default;

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;
        private readonly ReadScopeResolver _readScopeResolver;
        private readonly ParagraphSearchEngine _paragraphSearchEngine;

        public GrepDocumentTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _readScopeResolver = new ReadScopeResolver();
            _paragraphSearchEngine = new ParagraphSearchEngine();
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"keyword\":{\"type\":\"string\",\"minLength\":1,\"description\":\"要搜索的关键词或 .NET 正则表达式，必须是普通字符串。\"},\"use_regex\":{\"type\":\"boolean\",\"description\":\"true 时按标准 .NET 正则解释 keyword；非法正则会直接报错。\"},\"context_lines\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":20,\"description\":\"每个命中段落前后额外返回的上下文段落数。\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100,\"description\":\"最多返回的命中段落数；结果截断时会返回 diagnostics。\"},\"scope\":{\"type\":\"object\",\"additionalProperties\":false,\"description\":\"可选的搜索范围限制。scope 必须是 JSON 对象，不要传字符串化后的 JSON，否则会错误退化为全文搜索。\",\"properties\":{\"heading\":{\"type\":\"string\",\"description\":\"按标题限制搜索范围。与 from_para/to_para、around_cursor、selection_only 互斥。\"},\"from_para\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"起始段落，0-based，必须与 to_para 同时提供。\"},\"to_para\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"结束段落，0-based，必须不小于 from_para。\"},\"around_cursor\":{\"type\":\"boolean\",\"description\":\"true 表示以光标附近为搜索范围，与其它范围选择互斥。\"},\"context_window\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50,\"description\":\"around_cursor=true 时，表示光标前后各包含多少段。\"},\"selection_only\":{\"type\":\"boolean\",\"description\":\"true 表示仅在当前选区覆盖的段落范围内搜索，与其它范围选择互斥。\"}}}},\"required\":[\"keyword\"]}")
                .RootElement
                .Clone();
        }

        public string Name => "grep_document";

        public string Description => "搜索关键词或 .NET 正则表达式，返回总命中次数、每段全部命中偏移、所属章节与前后文。scope 必须是对象且范围选择互斥；scope.from_para/to_para 都是 0-based。";

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

            var keyword = ReadString(input, "keyword");
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return ToolCallResult.Error(Name, "keyword 不能为空。");
            }

            var useRegex = ReadBool(input, "use_regex", false);
            var contextLines = Math.Max(0, ReadNullableInt(input, "context_lines") ?? 2);
            var maxResults = Math.Max(1, ReadNullableInt(input, "max_results") ?? 10);
            var scopeInput = ReadScopeInput(input);

            var snapshotBuilder = new ReadOnlyDocumentSnapshotBuilder(_wordApplicationWrapper);
            var snapshot = await snapshotBuilder.BuildAsync(cancellationToken).ConfigureAwait(false);
            var diagnostics = new ReadDiagnostics();
            var resolvedScope = _readScopeResolver.Resolve(
                scopeInput,
                snapshot.ParagraphCount,
                snapshot.Headings,
                snapshot.CursorParagraphIndex,
                snapshot.Selection,
                diagnostics);
            var paragraphs = await snapshotBuilder
                .ReadParagraphsAsync(resolvedScope.FromParagraph, resolvedScope.ToParagraph, cancellationToken)
                .ConfigureAwait(false);
            var searchResult = _paragraphSearchEngine.Search(paragraphs, keyword, useRegex, maxResults);
            if (!string.IsNullOrWhiteSpace(searchResult.ErrorMessage))
            {
                return ToolCallResult.Error(Name, searchResult.ErrorMessage);
            }

            if (searchResult.IsTruncated)
            {
                diagnostics.IsPartial = true;
                diagnostics.AddWarning("命中段落数量超过 max_results，结果已截断。");
            }

            var resultPayloads = new List<object>();
            foreach (var match in searchResult.Results)
            {
                var before = await snapshotBuilder
                    .ReadParagraphsAsync(
                        Math.Max(resolvedScope.FromParagraph, match.Index - contextLines),
                        Math.Max(resolvedScope.FromParagraph, match.Index - 1),
                        cancellationToken)
                    .ConfigureAwait(false);
                var after = await snapshotBuilder
                    .ReadParagraphsAsync(
                        Math.Min(snapshot.ParagraphCount - 1, match.Index + 1),
                        Math.Min(resolvedScope.ToParagraph, match.Index + contextLines),
                        cancellationToken)
                    .ConfigureAwait(false);

                resultPayloads.Add(new
                {
                    para_index = match.Index,
                    text = match.Text,
                    highlight_offset = match.Matches.Count > 0 ? match.Matches[0].Start : -1,
                    matches = match.Matches,
                    section = DocumentSectionPathResolver.ResolveSectionPath(snapshot.Headings, match.Index),
                    context_before = BuildContextPayload(before, match.Index),
                    context_after = BuildContextPayload(after, match.Index)
                });
            }

            var payload = new
            {
                keyword,
                use_regex = useRegex,
                total_hit_paragraphs = searchResult.TotalHitParagraphs,
                total_matches = searchResult.TotalMatches,
                scope = new
                {
                    from_para = resolvedScope.FromParagraph,
                    to_para = resolvedScope.ToParagraph,
                    heading = resolvedScope.HeadingText
                },
                results = resultPayloads,
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

        private static ReadScope ReadScopeInput(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("scope", out var scopeToken))
            {
                return new ReadScope();
            }

            return new ReadScope
            {
                Heading = ReadString(scopeToken, "heading"),
                FromParagraph = ReadNullableInt(scopeToken, "from_para"),
                ToParagraph = ReadNullableInt(scopeToken, "to_para"),
                AroundCursor = ReadBool(scopeToken, "around_cursor", false),
                ContextWindow = Math.Max(1, ReadNullableInt(scopeToken, "context_window") ?? 5),
                SelectionOnly = ReadBool(scopeToken, "selection_only", false)
            };
        }

        private static string ValidateInputContract(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object)
            {
                return "grep_document 输入必须是 JSON 对象。";
            }

            if (!input.TryGetProperty("keyword", out var keyword)
                || keyword.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(keyword.GetString()))
            {
                return "keyword 必须是非空字符串。";
            }

            if (input.TryGetProperty("use_regex", out var useRegex)
                && useRegex.ValueKind != JsonValueKind.True
                && useRegex.ValueKind != JsonValueKind.False)
            {
                return "use_regex 必须是布尔值。";
            }

            if (input.TryGetProperty("scope", out var scope)
                && scope.ValueKind != JsonValueKind.Object)
            {
                return "scope 必须是 JSON 对象，不要传字符串化 JSON；否则搜索范围无法生效。";
            }

            if (input.TryGetProperty("context_lines", out var contextLines)
                && (!IsNonNegativeInt(contextLines) || contextLines.GetInt32() > 20))
            {
                return "context_lines 必须是 0 到 20 的整数。";
            }

            if (input.TryGetProperty("max_results", out var maxResults)
                && (!IsNonNegativeInt(maxResults) || maxResults.GetInt32() < 1 || maxResults.GetInt32() > 100))
            {
                return "max_results 必须是 1 到 100 的整数。";
            }

            if (scope.ValueKind == JsonValueKind.Object)
            {
                var scopeSelectors = 0;
                if (scope.TryGetProperty("heading", out var headingSelector)
                    && headingSelector.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(headingSelector.GetString()))
                {
                    scopeSelectors++;
                }

                var hasFrom = scope.TryGetProperty("from_para", out var from);
                var hasTo = scope.TryGetProperty("to_para", out var to);
                if (hasFrom != hasTo)
                {
                    return "scope.from_para 和 scope.to_para 必须同时提供。";
                }

                if (hasFrom && (!IsNonNegativeInt(from) || !IsNonNegativeInt(to)))
                {
                    return "scope.from_para/to_para 必须是 0-based 非负整数。";
                }

                if (hasFrom && from.GetInt32() > to.GetInt32())
                {
                    return "scope.to_para 必须大于或等于 scope.from_para。";
                }

                if (hasFrom)
                {
                    scopeSelectors++;
                }

                if (scope.TryGetProperty("heading", out var heading)
                    && heading.ValueKind != JsonValueKind.String)
                {
                    return "scope.heading 必须是字符串。";
                }

                foreach (var name in new[] { "around_cursor", "selection_only" })
                {
                    if (scope.TryGetProperty(name, out var flag)
                        && flag.ValueKind != JsonValueKind.True
                        && flag.ValueKind != JsonValueKind.False)
                    {
                        return $"scope.{name} 必须是布尔值。";
                    }

                    if (scope.TryGetProperty(name, out flag) && flag.ValueKind == JsonValueKind.True)
                    {
                        scopeSelectors++;
                    }
                }

                if (scopeSelectors > 1)
                {
                    return "scope 范围选择互斥：heading、from_para/to_para、around_cursor、selection_only 只能选择一种。";
                }
            }

            return string.Empty;
        }

        private static bool IsNonNegativeInt(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out var value)
                && value >= 0;
        }

        private static IEnumerable<object> BuildContextPayload(
            IReadOnlyList<ParagraphSnapshot> paragraphs,
            int currentParagraphIndex)
        {
            foreach (var paragraph in paragraphs)
            {
                if (paragraph.Index == currentParagraphIndex)
                {
                    continue;
                }

                yield return new
                {
                    index = paragraph.Index,
                    text = paragraph.Text
                };
            }
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
