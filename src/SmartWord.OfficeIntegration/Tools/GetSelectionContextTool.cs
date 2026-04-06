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
    /// 获取当前选区以及前后文段落。
    /// </summary>
    public sealed class GetSelectionContextTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;

        public GetSelectionContextTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}")
                .RootElement
                .Clone();
        }

        public string Name => "get_selection_context";

        public string Description => "读取当前选区文本、所在段落与前后文。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = input;
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var selectionInfo = await _wordApplicationWrapper.GetSelectionInfoAsync().ConfigureAwait(false);
            var headings = await _wordApplicationWrapper.GetHeadingsAsync().ConfigureAwait(false);

            if (!selectionInfo.HasSelection || selectionInfo.ParagraphIndex < 0)
            {
                return ToolCallResult.Ok(JsonSerializer.Serialize(new
                {
                    selection = new
                    {
                        has_selection = false,
                        text = string.Empty,
                        para_index = -1,
                        char_start = -1,
                        char_end = -1
                    },
                    context = new
                    {
                        paragraph_full = string.Empty,
                        section = string.Empty,
                        prev_paragraph = string.Empty,
                        next_paragraph = string.Empty
                    }
                }, JsonOptions));
            }

            var currentParagraph = await _wordApplicationWrapper.GetParagraphTextAsync(selectionInfo.ParagraphIndex).ConfigureAwait(false);
            var previousParagraph = await _wordApplicationWrapper.GetParagraphTextAsync(selectionInfo.ParagraphIndex - 1).ConfigureAwait(false);
            var nextParagraph = await _wordApplicationWrapper.GetParagraphTextAsync(selectionInfo.ParagraphIndex + 1).ConfigureAwait(false);

            var payload = new
            {
                selection = new
                {
                    has_selection = selectionInfo.HasSelection,
                    text = selectionInfo.Text,
                    para_index = selectionInfo.ParagraphIndex,
                    char_start = selectionInfo.CharStart,
                    char_end = selectionInfo.CharEnd
                },
                context = new
                {
                    paragraph_full = currentParagraph,
                    section = string.Join(" > ", headings
                        .Where(item => item.ParagraphIndex <= selectionInfo.ParagraphIndex)
                        .OrderByDescending(item => item.ParagraphIndex)
                        .Take(3)
                        .OrderBy(item => item.Level)
                        .Select(item => item.Text)),
                    prev_paragraph = previousParagraph,
                    next_paragraph = nextParagraph
                }
            };

            return ToolCallResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
        }
    }
}
