using System;
using System.Linq;
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
    /// 获取当前选区以及前后文段落。
    /// </summary>
    public sealed class GetSelectionContextTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;

        public GetSelectionContextTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"description\":\"无需参数。返回的 para_index、start_para_index、end_para_index、paragraph_index 一律使用 0-based。\"}")
                .RootElement
                .Clone();
        }

        public string Name => "get_selection_context";

        public string Description => "读取当前选区文本、所在段落与前后文；若当前无显式选区，则回退到光标所在段落。返回的段落索引一律使用 0-based。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public bool IsVisibleToModel => true;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = input;
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var snapshotBuilder = new ReadOnlyDocumentSnapshotBuilder(_wordApplicationWrapper);
            var snapshot = await snapshotBuilder.BuildAsync(cancellationToken).ConfigureAwait(false);
            var selection = snapshot.Selection ?? new SelectionSnapshot();
            var diagnostics = new ReadDiagnostics();

            if (selection.ParagraphIndex < 0)
            {
                return ToolCallResult.Ok(JsonSerializer.Serialize(new
                {
                    selection = new
                    {
                        has_selection = false,
                        text = string.Empty,
                        para_index = -1,
                        start_para_index = -1,
                        end_para_index = -1,
                        is_multi_paragraph = false,
                        char_start = -1,
                        char_end = -1
                    },
                    context = new
                    {
                        paragraph_index = -1,
                        paragraph_full = string.Empty,
                        section = string.Empty,
                        prev_paragraph = string.Empty,
                        next_paragraph = string.Empty
                    },
                    diagnostics = new
                    {
                        is_partial = false,
                        warnings = new[] { "当前无法定位光标或选区所在段落。" }
                    }
                }, JsonOptions));
            }

            if (!selection.HasSelection)
            {
                diagnostics.AddWarning("当前无显式选区，已回退到光标所在段落。");
            }

            if (selection.IsMultiParagraph)
            {
                diagnostics.IsPartial = true;
                diagnostics.AddWarning("当前选区跨越多个段落，段内字符偏移仅基于起始段计算。");
            }

            var paragraphs = await snapshotBuilder
                .ReadParagraphsAsync(
                    Math.Max(0, selection.ParagraphIndex - 1),
                    Math.Min(snapshot.ParagraphCount - 1, selection.ParagraphIndex + 1),
                    cancellationToken)
                .ConfigureAwait(false);
            var currentParagraph = paragraphs.FirstOrDefault(item => item.Index == selection.ParagraphIndex)?.Text ?? string.Empty;
            var previousParagraph = paragraphs.FirstOrDefault(item => item.Index == selection.ParagraphIndex - 1)?.Text ?? string.Empty;
            var nextParagraph = paragraphs.FirstOrDefault(item => item.Index == selection.ParagraphIndex + 1)?.Text ?? string.Empty;

            var payload = new
            {
                selection = new
                {
                    has_selection = selection.HasSelection,
                    text = selection.Text,
                    para_index = selection.ParagraphIndex,
                    start_para_index = selection.StartParagraphIndex,
                    end_para_index = selection.EndParagraphIndex,
                    is_multi_paragraph = selection.IsMultiParagraph,
                    char_start = selection.CharStart,
                    char_end = selection.CharEnd
                },
                context = new
                {
                    paragraph_index = selection.ParagraphIndex,
                    paragraph_full = currentParagraph,
                    section = DocumentSectionPathResolver.ResolveSectionPath(snapshot.Headings, selection.ParagraphIndex),
                    prev_paragraph = previousParagraph,
                    next_paragraph = nextParagraph
                },
                diagnostics = BuildDiagnosticsPayload(diagnostics)
            };

            return ToolCallResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
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
