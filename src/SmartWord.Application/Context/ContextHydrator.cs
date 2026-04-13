using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.Reading;
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

            var snapshotBuilder = new ReadOnlyDocumentSnapshotBuilder(_wordApplicationWrapper);
            var snapshot = await SafeReadAsync(
                () => snapshotBuilder.BuildAsync(cancellationToken),
                null).ConfigureAwait(false);
            if (snapshot == null)
            {
                snapshot = new OfficeIntegration.Models.DocumentReadSnapshot();
            }

            return new DocumentContext
            {
                DocumentPath = snapshot.DocumentPath,
                DocumentName = string.IsNullOrWhiteSpace(snapshot.DocumentPath) ? string.Empty : Path.GetFileName(snapshot.DocumentPath),
                DocumentStatus = snapshot.Status,
                ParagraphCount = snapshot.ParagraphCount,
                WordCount = snapshot.WordCount,
                CurrentPageNumber = snapshot.CurrentPage,
                TotalPages = snapshot.TotalPages,
                Complexity = snapshot.Complexity,
                CursorParagraphIndex = snapshot.CursorParagraphIndex,
                HasSelection = snapshot.Selection.HasSelection,
                SelectedText = snapshot.Selection.Text,
                SelectionParagraphIndex = snapshot.Selection.ParagraphIndex,
                Headings = new System.Collections.Generic.List<DocumentHeading>(snapshot.Headings),
                TableCount = snapshot.Stats.TableCount,
                ImageCount = snapshot.Stats.ImageCount,
                AnnotationCount = snapshot.Stats.AnnotationCount
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
    }
}
