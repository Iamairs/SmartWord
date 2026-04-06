using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.OfficeIntegration.Tools
{
    /// <summary>
    /// 在文档段落中执行关键词或正则搜索。
    /// </summary>
    public sealed class GrepDocumentTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;

        public GrepDocumentTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"keyword\":{\"type\":\"string\"},\"use_regex\":{\"type\":\"boolean\"},\"context_lines\":{\"type\":\"integer\"},\"max_results\":{\"type\":\"integer\"}}}")
                .RootElement
                .Clone();
        }

        public string Name => "grep_document";

        public string Description => "搜索关键词并返回命中段落及前后文。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var keyword = ReadString(input, "keyword");
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return ToolCallResult.Error(Name, "keyword 不能为空。");
            }

            var useRegex = ReadBool(input, "use_regex", false);
            var contextLines = Math.Max(0, ReadNullableInt(input, "context_lines") ?? 2);
            var maxResults = Math.Max(1, ReadNullableInt(input, "max_results") ?? 10);

            var matches = await _wordApplicationWrapper
                .SearchTextAsync(keyword, useRegex, maxResults)
                .ConfigureAwait(false);
            var headings = await _wordApplicationWrapper.GetHeadingsAsync().ConfigureAwait(false);

            var resultPayloads = new List<object>();
            foreach (var match in matches)
            {
                var before = await _wordApplicationWrapper
                    .ReadParagraphsAsync(
                        Math.Max(0, match.ParagraphIndex - contextLines),
                        Math.Max(0, match.ParagraphIndex - 1))
                    .ConfigureAwait(false);
                var after = await _wordApplicationWrapper
                    .ReadParagraphsAsync(
                        match.ParagraphIndex + 1,
                        match.ParagraphIndex + contextLines)
                    .ConfigureAwait(false);

                resultPayloads.Add(new
                {
                    para_index = match.ParagraphIndex,
                    text = match.ParagraphText,
                    highlight_offset = match.CharOffset,
                    section = ResolveSection(headings, match.ParagraphIndex),
                    context_before = before.Select(item => new { index = item.Index, text = item.Text }),
                    context_after = after.Select(item => new { index = item.Index, text = item.Text })
                });
            }

            var payload = new
            {
                keyword,
                total_matches = matches.Count,
                results = resultPayloads
            };

            return ToolCallResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
        }

        private static string ResolveSection(IReadOnlyList<DocumentHeading> headings, int paragraphIndex)
        {
            if (headings == null || headings.Count == 0)
            {
                return string.Empty;
            }

            var matched = headings
                .Where(item => item.ParagraphIndex <= paragraphIndex)
                .OrderByDescending(item => item.ParagraphIndex)
                .Take(3)
                .OrderBy(item => item.Level)
                .Select(item => item.Text)
                .ToArray();
            return string.Join(" > ", matched);
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
    }
}
