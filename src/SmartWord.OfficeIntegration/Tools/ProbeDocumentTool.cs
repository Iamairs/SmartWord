using System;
using System.IO;
using System.Linq;
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
    /// 获取文档整体结构、统计信息与当前光标状态。
    /// </summary>
    public sealed class ProbeDocumentTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = ToolJsonOptions.Default;

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;

        public ProbeDocumentTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"include_stats\":{\"type\":\"boolean\",\"description\":\"是否返回 table_count/image_count/annotation_count 等统计信息。\"},\"include_headings\":{\"type\":\"boolean\",\"description\":\"是否返回 outline 标题结构。返回的 para_index 一律使用 0-based。\"}}}")
                .RootElement
                .Clone();
        }

        public string Name => "probe_document";

        public string Description => "获取文档全局结构、统计信息、光标位置、最近标题与选区信息。返回中的 para_index 一律使用 0-based。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public bool IsVisibleToModel => true;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var includeStats = ReadBool(input, "include_stats", true);
            var includeHeadings = ReadBool(input, "include_headings", true);

            var snapshotBuilder = new ReadOnlyDocumentSnapshotBuilder(_wordApplicationWrapper);
            var snapshot = await snapshotBuilder.BuildAsync(cancellationToken).ConfigureAwait(false);
            var headings = includeHeadings
                ? snapshot.Headings
                : Array.Empty<DocumentHeading>();
            var stats = includeStats
                ? snapshot.Stats
                : new DocumentStructureStats();
            var nearestHeading = DocumentSectionPathResolver.ResolveNearestHeading(headings, snapshot.CursorParagraphIndex);

            var payload = new
            {
                document = new
                {
                    name = string.IsNullOrWhiteSpace(snapshot.DocumentPath) ? string.Empty : Path.GetFileName(snapshot.DocumentPath),
                    path = snapshot.DocumentPath,
                    word_count = snapshot.WordCount,
                    paragraph_count = snapshot.ParagraphCount,
                    table_count = stats.TableCount,
                    image_count = stats.ImageCount,
                    annotation_count = stats.AnnotationCount,
                    complexity = snapshot.Complexity
                },
                status = new
                {
                    is_writable = snapshot.Status.IsWritable,
                    is_read_only = snapshot.Status.IsReadOnly,
                    is_password_protected = snapshot.Status.IsPasswordProtected,
                    is_track_changes_enforced = snapshot.Status.IsTrackChangesEnforced,
                    message = snapshot.Status.GetUserFriendlyMessage()
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
                    para_index = snapshot.CursorParagraphIndex,
                    current_page = snapshot.CurrentPage,
                    total_pages = snapshot.TotalPages,
                    nearest_heading = nearestHeading == null
                        ? null
                        : new
                        {
                            text = nearestHeading.Text,
                            para_index = nearestHeading.ParagraphIndex
                        }
                },
                selection = new
                {
                    has_selection = snapshot.Selection.HasSelection,
                    text = snapshot.Selection.Text,
                    para_index = snapshot.Selection.ParagraphIndex,
                    char_start = snapshot.Selection.CharStart,
                    char_end = snapshot.Selection.CharEnd,
                    start_para_index = snapshot.Selection.StartParagraphIndex,
                    end_para_index = snapshot.Selection.EndParagraphIndex,
                    is_multi_paragraph = snapshot.Selection.IsMultiParagraph
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
    }
}
