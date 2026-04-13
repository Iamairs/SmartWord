using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.OfficeIntegration.Models;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.OfficeIntegration.Reading
{
    /// <summary>
    /// 构建单次工具调用期间可复用的只读文档快照。
    /// </summary>
    public sealed class ReadOnlyDocumentSnapshotBuilder
    {
        private readonly Dictionary<int, ParagraphSnapshot> _paragraphCache =
            new Dictionary<int, ParagraphSnapshot>();

        private readonly WordApplicationWrapper _wordApplicationWrapper;

        public ReadOnlyDocumentSnapshotBuilder(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper ?? throw new ArgumentNullException(nameof(wordApplicationWrapper));
        }

        public async Task<DocumentReadSnapshot> BuildAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var documentPath = await _wordApplicationWrapper.GetActiveDocumentPath().ConfigureAwait(false);
            var documentStatus = await _wordApplicationWrapper.GetDocumentStatusAsync().ConfigureAwait(false);
            var paragraphCount = await _wordApplicationWrapper.GetParagraphCountAsync().ConfigureAwait(false);
            var wordCount = await _wordApplicationWrapper.GetWordCountAsync().ConfigureAwait(false);
            var pageInfo = await _wordApplicationWrapper.GetPageInfoAsync().ConfigureAwait(false);
            var cursorParagraphIndex = await _wordApplicationWrapper.GetCursorParagraphIndexAsync().ConfigureAwait(false);
            var selection = await _wordApplicationWrapper.GetSelectionSnapshotAsync().ConfigureAwait(false);
            var headings = await _wordApplicationWrapper.GetHeadingsAsync().ConfigureAwait(false);
            var stats = await _wordApplicationWrapper.GetDocumentStatsAsync().ConfigureAwait(false);

            return new DocumentReadSnapshot
            {
                DocumentPath = documentPath,
                DocumentName = string.IsNullOrWhiteSpace(documentPath) ? string.Empty : Path.GetFileName(documentPath),
                Status = documentStatus,
                ParagraphCount = paragraphCount,
                WordCount = wordCount,
                CurrentPage = pageInfo.CurrentPage,
                TotalPages = pageInfo.TotalPages,
                Complexity = ResolveComplexity(wordCount),
                CursorParagraphIndex = cursorParagraphIndex,
                Selection = selection ?? new SelectionSnapshot(),
                Headings = headings?.ToList() ?? new List<SmartWord.Core.Models.DocumentHeading>(),
                Stats = stats ?? new DocumentStructureStats()
            };
        }

        public async Task<IReadOnlyList<ParagraphSnapshot>> ReadParagraphsAsync(
            int fromParagraph,
            int toParagraph,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var start = Math.Max(0, fromParagraph);
            var end = Math.Max(start, toParagraph);
            var missingIndexes = Enumerable.Range(start, end - start + 1)
                .Where(index => !_paragraphCache.ContainsKey(index))
                .ToArray();
            if (missingIndexes.Length > 0)
            {
                var paragraphTuples = await _wordApplicationWrapper
                    .ReadParagraphsAsync(missingIndexes[0], missingIndexes[missingIndexes.Length - 1])
                    .ConfigureAwait(false);
                foreach (var paragraphTuple in paragraphTuples)
                {
                    _paragraphCache[paragraphTuple.Index] = new ParagraphSnapshot
                    {
                        Index = paragraphTuple.Index,
                        Style = paragraphTuple.Style,
                        Text = paragraphTuple.Text
                    };
                }
            }

            return Enumerable.Range(start, end - start + 1)
                .Where(index => _paragraphCache.ContainsKey(index))
                .Select(index => _paragraphCache[index])
                .ToList();
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
