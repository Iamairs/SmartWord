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
    /// 按标题、段落范围或光标附近读取文档内容。
    /// </summary>
    public sealed class ReadSectionTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;

        public ReadSectionTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"heading\":{\"type\":\"string\"},\"include_subsections\":{\"type\":\"boolean\"},\"from_para\":{\"type\":\"integer\"},\"to_para\":{\"type\":\"integer\"},\"around_cursor\":{\"type\":\"boolean\"},\"context_window\":{\"type\":\"integer\"},\"include_formatting\":{\"type\":\"boolean\"},\"max_tokens\":{\"type\":\"integer\"}}}")
                .RootElement
                .Clone();
        }

        public string Name => "read_section";

        public string Description => "按标题、段落范围或光标附近读取指定片段。";

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

            var paragraphCount = await _wordApplicationWrapper.GetParagraphCountAsync().ConfigureAwait(false);
            if (paragraphCount <= 0)
            {
                return ToolCallResult.Ok(JsonSerializer.Serialize(new
                {
                    range = new { from = 0, to = 0, heading = string.Empty },
                    paragraphs = Array.Empty<object>(),
                    truncated = false,
                    token_estimate = 0
                }, JsonOptions));
            }

            var resolvedRange = await ResolveRangeAsync(
                heading,
                includeSubsections,
                fromPara,
                toPara,
                aroundCursor,
                contextWindow,
                paragraphCount).ConfigureAwait(false);

            var paragraphSnapshots = await _wordApplicationWrapper
                .ReadParagraphsAsync(resolvedRange.FromParagraph, resolvedRange.ToParagraph)
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
                token_estimate = tokenEstimate
            };

            return ToolCallResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
        }

        private async Task<(int FromParagraph, int ToParagraph, string HeadingText)> ResolveRangeAsync(
            string heading,
            bool includeSubsections,
            int? fromPara,
            int? toPara,
            bool aroundCursor,
            int contextWindow,
            int paragraphCount)
        {
            if (!string.IsNullOrWhiteSpace(heading))
            {
                var headings = await _wordApplicationWrapper.GetHeadingsAsync().ConfigureAwait(false);
                var matchedHeading = headings.FirstOrDefault(item =>
                    string.Equals(item.Text, heading, StringComparison.OrdinalIgnoreCase))
                    ?? headings.FirstOrDefault(item =>
                        item.Text.IndexOf(heading, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(matchedHeading.Text))
                {
                    var endParagraph = paragraphCount - 1;
                    foreach (var nextHeading in headings.Where(item => item.ParagraphIndex > matchedHeading.ParagraphIndex))
                    {
                        var shouldStop = includeSubsections
                            ? nextHeading.Level <= matchedHeading.Level
                            : true;
                        if (shouldStop)
                        {
                            endParagraph = Math.Max(matchedHeading.ParagraphIndex, nextHeading.ParagraphIndex - 1);
                            break;
                        }
                    }

                    return (matchedHeading.ParagraphIndex, endParagraph, matchedHeading.Text);
                }
            }

            if (fromPara.HasValue || toPara.HasValue)
            {
                var start = Math.Max(0, fromPara ?? 0);
                var end = Math.Min(paragraphCount - 1, toPara ?? start);
                if (end < start)
                {
                    end = start;
                }

                return (start, end, string.Empty);
            }

            if (aroundCursor)
            {
                var cursor = await _wordApplicationWrapper.GetCursorParagraphIndexAsync().ConfigureAwait(false);
                var safeCursor = cursor < 0 ? 0 : cursor;
                return (
                    Math.Max(0, safeCursor - contextWindow),
                    Math.Min(paragraphCount - 1, safeCursor + contextWindow),
                    string.Empty);
            }

            return (0, Math.Min(paragraphCount - 1, contextWindow * 2), string.Empty);
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
