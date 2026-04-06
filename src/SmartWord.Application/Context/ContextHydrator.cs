using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 组装最基础的文档上下文快照。
    /// </summary>
    public class ContextHydrator : IContextHydrator
    {
        private readonly WordApplicationWrapper _wordApplicationWrapper;

        public ContextHydrator(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
        }

        public async Task<DocumentContext> HydrateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var documentPath = await SafeReadAsync(
                () => _wordApplicationWrapper.GetActiveDocumentPath(),
                string.Empty).ConfigureAwait(false);
            var documentStatus = await SafeReadAsync(
                () => _wordApplicationWrapper.GetDocumentStatusAsync(),
                new DocumentStatus()).ConfigureAwait(false);
            var paragraphCount = await SafeReadAsync(
                () => _wordApplicationWrapper.GetParagraphCountAsync(),
                0).ConfigureAwait(false);
            var wordCount = await SafeReadAsync(
                () => _wordApplicationWrapper.GetWordCountAsync(),
                0).ConfigureAwait(false);
            var pageInfo = await SafeReadAsync(
                () => _wordApplicationWrapper.GetPageInfoAsync(),
                (0, 0)).ConfigureAwait(false);
            var cursorParagraphIndex = await SafeReadAsync(
                () => _wordApplicationWrapper.GetCursorParagraphIndexAsync(),
                -1).ConfigureAwait(false);
            var selectionInfo = await SafeReadAsync(
                () => _wordApplicationWrapper.GetSelectionInfoAsync(),
                (false, string.Empty, -1, -1, -1)).ConfigureAwait(false);
            var headings = await SafeReadAsync(
                () => _wordApplicationWrapper.GetHeadingsAsync(),
                new System.Collections.Generic.List<DocumentHeading>()).ConfigureAwait(false);
            var stats = await SafeReadAsync(
                () => _wordApplicationWrapper.GetDocumentStatsAsync(),
                (0, 0)).ConfigureAwait(false);

            return new DocumentContext
            {
                DocumentPath = documentPath,
                DocumentName = string.IsNullOrWhiteSpace(documentPath) ? string.Empty : Path.GetFileName(documentPath),
                DocumentStatus = documentStatus,
                ParagraphCount = paragraphCount,
                WordCount = wordCount,
                CurrentPageNumber = pageInfo.Item1,
                TotalPages = pageInfo.Item2,
                Complexity = ResolveComplexity(wordCount),
                CursorParagraphIndex = cursorParagraphIndex,
                HasSelection = selectionInfo.Item1,
                SelectedText = selectionInfo.Item2,
                SelectionParagraphIndex = selectionInfo.Item3,
                Headings = headings,
                TableCount = stats.Item1,
                ImageCount = stats.Item2
            };
        }

        private static async Task<T> SafeReadAsync<T>(Func<Task<T>> reader, T defaultValue)
        {
            try
            {
                return await reader().ConfigureAwait(false);
            }
            catch
            {
                return defaultValue;
            }
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
