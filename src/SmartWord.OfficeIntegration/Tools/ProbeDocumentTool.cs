using System;
using System.IO;
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
    /// 获取文档整体结构、统计信息与当前光标状态。
    /// </summary>
    public sealed class ProbeDocumentTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;

        public ProbeDocumentTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"include_styles\":{\"type\":\"boolean\"},\"include_stats\":{\"type\":\"boolean\"},\"include_headings\":{\"type\":\"boolean\"}}}")
                .RootElement
                .Clone();
        }

        public string Name => "probe_document";

        public string Description => "获取文档全局结构、统计信息、光标位置和选区信息。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var includeStats = ReadBool(input, "include_stats", true);
            var includeHeadings = ReadBool(input, "include_headings", true);

            var documentPath = await _wordApplicationWrapper.GetActiveDocumentPath().ConfigureAwait(false);
            var paragraphCount = await _wordApplicationWrapper.GetParagraphCountAsync().ConfigureAwait(false);
            var wordCount = await _wordApplicationWrapper.GetWordCountAsync().ConfigureAwait(false);
            var pageInfo = await _wordApplicationWrapper.GetPageInfoAsync().ConfigureAwait(false);
            var cursorParagraphIndex = await _wordApplicationWrapper.GetCursorParagraphIndexAsync().ConfigureAwait(false);
            var selectionInfo = await _wordApplicationWrapper.GetSelectionInfoAsync().ConfigureAwait(false);
            var status = await _wordApplicationWrapper.GetDocumentStatusAsync().ConfigureAwait(false);
            var headings = includeHeadings
                ? await _wordApplicationWrapper.GetHeadingsAsync().ConfigureAwait(false)
                : new System.Collections.Generic.List<DocumentHeading>();
            var stats = includeStats
                ? await _wordApplicationWrapper.GetDocumentStatsAsync().ConfigureAwait(false)
                : (0, 0);

            var payload = new
            {
                document = new
                {
                    name = string.IsNullOrWhiteSpace(documentPath) ? string.Empty : Path.GetFileName(documentPath),
                    path = documentPath,
                    word_count = wordCount,
                    paragraph_count = paragraphCount,
                    table_count = stats.Item1,
                    image_count = stats.Item2,
                    complexity = ResolveComplexity(wordCount)
                },
                status = new
                {
                    is_writable = status.IsWritable,
                    is_read_only = status.IsReadOnly,
                    is_password_protected = status.IsPasswordProtected,
                    is_track_changes_enforced = status.IsTrackChangesEnforced,
                    message = status.GetUserFriendlyMessage()
                },
                outline = headings.Select(item => new
                {
                    level = item.Level,
                    text = item.Text,
                    para_index = item.ParagraphIndex,
                    child_count = item.ChildCount
                }),
                cursor = new
                {
                    para_index = cursorParagraphIndex,
                    current_page = pageInfo.CurrentPage,
                    total_pages = pageInfo.TotalPages
                },
                selection = new
                {
                    has_selection = selectionInfo.HasSelection,
                    text = selectionInfo.Text,
                    para_index = selectionInfo.ParagraphIndex,
                    char_start = selectionInfo.CharStart,
                    char_end = selectionInfo.CharEnd
                }
            };

            return ToolCallResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
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

        private static string ResolveComplexity(int wordCount)
        {
            if (wordCount < 1000)
            {
                return "small";
            }

            return wordCount < 10000 ? "medium" : "large";
        }
    }
}
