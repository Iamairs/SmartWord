using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.OfficeIntegration.WordWrappers
{
    /// <summary>
    /// 负责把对 Word 的访问切回宿主 UI 线程执行。
    /// </summary>
    public sealed class WordApplicationWrapper : IDisposable
    {
        private readonly dynamic _wordApplication;
        private readonly Control _uiThreadInvoker;
        private readonly int _ownerThreadId;

        public WordApplicationWrapper(object wordApplication)
        {
            _wordApplication = wordApplication ?? throw new ArgumentNullException(nameof(wordApplication));
            _ownerThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

            // 在创建包装器时显式绑定一个 WinForms 控件句柄，后续统一用它把 COM 访问切回宿主 UI 线程。
            _uiThreadInvoker = new Control();
            var handle = _uiThreadInvoker.Handle;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (System.Threading.Thread.CurrentThread.ManagedThreadId == _ownerThreadId)
            {
                return Task.FromResult(action());
            }

            var taskCompletionSource = new TaskCompletionSource<T>();
            _uiThreadInvoker.BeginInvoke(new Action(() =>
            {
                try
                {
                    taskCompletionSource.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    taskCompletionSource.TrySetException(ex);
                }
            }));

            return taskCompletionSource.Task;
        }

        public async Task InvokeAsync(Action action)
        {
            await InvokeAsync(() =>
            {
                action();
                return true;
            });
        }

        public Task<string> GetActiveDocumentPath()
        {
            return InvokeAsync<string>(() =>
            {
                dynamic activeDocument = null;
                try
                {
                    activeDocument = _wordApplication.ActiveDocument;
                    return activeDocument == null ? string.Empty : Convert.ToString(activeDocument.FullName);
                }
                catch
                {
                    return string.Empty;
                }
                finally
                {
                    TryReleaseComObject(activeDocument);
                }
            });
        }

        public Task<DocumentStatus> GetDocumentStatusAsync()
        {
            return InvokeAsync(() =>
            {
                dynamic activeDocument = null;
                try
                {
                    activeDocument = _wordApplication.ActiveDocument;
                    if (activeDocument == null)
                    {
                        return new DocumentStatus
                        {
                            IsWritable = false,
                            Reason = "当前没有活动文档。"
                        };
                    }

                    var isReadOnly = SafeConvertToBool(() => activeDocument.ReadOnly);
                    var hasProtection = SafeConvertToInt(() => activeDocument.ProtectionType) > 0;
                    var trackRevisions = SafeConvertToBool(() => activeDocument.TrackRevisions);

                    return new DocumentStatus
                    {
                        IsWritable = !isReadOnly && !hasProtection,
                        IsReadOnly = isReadOnly,
                        IsPasswordProtected = hasProtection,
                        IsTrackChangesEnforced = trackRevisions,
                        Reason = isReadOnly
                            ? "当前活动文档处于只读状态。"
                            : (hasProtection ? "当前活动文档处于受保护状态。" : string.Empty)
                    };
                }
                catch (Exception ex)
                {
                    return new DocumentStatus
                    {
                        IsWritable = false,
                        Reason = ex.Message
                    };
                }
                finally
                {
                    TryReleaseComObject(activeDocument);
                }
            });
        }

        public async Task<IUndoScope> BeginTaskUndoAsync(string operationName)
        {
            return await InvokeAsync<IUndoScope>(() =>
            {
                var undoScope = new UndoRecordWrapper(_wordApplication);
                undoScope.BeginTransaction(operationName);
                return undoScope;
            });
        }

        public Task<int> GetParagraphCountAsync()
        {
            return InvokeAsync<int>(() =>
            {
                dynamic document = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    return document == null ? 0 : Convert.ToInt32(document.Paragraphs.Count);
                }
                catch
                {
                    return 0;
                }
                finally
                {
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<int> GetWordCountAsync()
        {
            return InvokeAsync<int>(() =>
            {
                dynamic document = null;
                dynamic words = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    if (document == null)
                    {
                        return 0;
                    }

                    words = document.Words;
                    return words == null ? 0 : Convert.ToInt32(words.Count);
                }
                catch
                {
                    return 0;
                }
                finally
                {
                    TryReleaseComObject(words);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<(int CurrentPage, int TotalPages)> GetPageInfoAsync()
        {
            return InvokeAsync<(int CurrentPage, int TotalPages)>(() =>
            {
                dynamic document = null;
                dynamic selection = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    selection = _wordApplication.Selection;
                    var currentPage = selection == null ? 0 : SafeConvertToInt(() => selection.get_Information(3));
                    var totalPages = document == null ? 0 : SafeConvertToInt(() => document.ComputeStatistics(2));
                    return (currentPage, totalPages);
                }
                catch
                {
                    return (0, 0);
                }
                finally
                {
                    TryReleaseComObject(selection);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<int> GetCursorParagraphIndexAsync()
        {
            return InvokeAsync<int>(() =>
            {
                dynamic document = null;
                dynamic selection = null;
                dynamic range = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    selection = _wordApplication.Selection;
                    range = selection == null ? null : selection.Range;
                    return document == null || range == null
                        ? -1
                        : GetParagraphIndexFromRangeStartInternal(document, SafeConvertToInt(() => range.Start));
                }
                catch
                {
                    return -1;
                }
                finally
                {
                    TryReleaseComObject(range);
                    TryReleaseComObject(selection);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<(bool HasSelection, string Text, int ParagraphIndex, int CharStart, int CharEnd)> GetSelectionInfoAsync()
        {
            return InvokeAsync<(bool HasSelection, string Text, int ParagraphIndex, int CharStart, int CharEnd)>(() =>
            {
                dynamic document = null;
                dynamic selection = null;
                dynamic range = null;
                dynamic paragraph = null;
                dynamic paragraphRange = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    selection = _wordApplication.Selection;
                    range = selection == null ? null : selection.Range;
                    if (document == null || range == null)
                    {
                        return (false, string.Empty, -1, -1, -1);
                    }

                    var selectionText = NormalizeParagraphText(Convert.ToString(range.Text));
                    var hasSelection = SafeConvertToInt(() => range.Start) != SafeConvertToInt(() => range.End)
                        && !string.IsNullOrWhiteSpace(selectionText);

                    paragraph = range.Paragraphs == null ? null : range.Paragraphs[1];
                    paragraphRange = paragraph == null ? null : paragraph.Range;
                    var paragraphStart = paragraphRange == null ? 0 : SafeConvertToInt(() => paragraphRange.Start);
                    var paragraphIndex = paragraphRange == null
                        ? -1
                        : GetParagraphIndexFromRangeStartInternal(document, paragraphStart);

                    var charStart = paragraphRange == null
                        ? -1
                        : Math.Max(0, SafeConvertToInt(() => range.Start) - paragraphStart);
                    var charEnd = paragraphRange == null
                        ? -1
                        : Math.Max(charStart, SafeConvertToInt(() => range.End) - paragraphStart);

                    return (hasSelection, selectionText, paragraphIndex, charStart, charEnd);
                }
                catch
                {
                    return (false, string.Empty, -1, -1, -1);
                }
                finally
                {
                    TryReleaseComObject(paragraphRange);
                    TryReleaseComObject(paragraph);
                    TryReleaseComObject(range);
                    TryReleaseComObject(selection);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<List<DocumentHeading>> GetHeadingsAsync()
        {
            return InvokeAsync<List<DocumentHeading>>(() =>
            {
                var headings = new List<DocumentHeading>();
                dynamic document = null;
                dynamic paragraphs = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    if (document == null)
                    {
                        return headings;
                    }

                    paragraphs = document.Paragraphs;
                    var paragraphCount = paragraphs == null ? 0 : Convert.ToInt32(paragraphs.Count);
                    for (var index = 1; index <= paragraphCount; index++)
                    {
                        dynamic paragraph = null;
                        dynamic range = null;
                        try
                        {
                            paragraph = paragraphs[index];
                            range = paragraph == null ? null : paragraph.Range;
                            var outlineLevel = SafeConvertToInt(() => paragraph.OutlineLevel);
                            if (outlineLevel < 1 || outlineLevel > 9)
                            {
                                continue;
                            }

                            var text = NormalizeParagraphText(range == null ? string.Empty : Convert.ToString(range.Text));
                            if (string.IsNullOrWhiteSpace(text))
                            {
                                continue;
                            }

                            headings.Add(new DocumentHeading
                            {
                                Level = outlineLevel,
                                Text = text,
                                ParagraphIndex = index - 1
                            });
                        }
                        finally
                        {
                            TryReleaseComObject(range);
                            TryReleaseComObject(paragraph);
                        }
                    }

                    for (var i = 0; i < headings.Count; i++)
                    {
                        var childCount = 0;
                        for (var j = i + 1; j < headings.Count; j++)
                        {
                            if (headings[j].Level <= headings[i].Level)
                            {
                                break;
                            }

                            if (headings[j].Level == headings[i].Level + 1)
                            {
                                childCount++;
                            }
                        }

                        headings[i].ChildCount = childCount;
                    }

                    return headings;
                }
                catch
                {
                    return headings;
                }
                finally
                {
                    TryReleaseComObject(paragraphs);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<List<(int Index, string Style, string Text)>> ReadParagraphsAsync(int fromIndex, int toIndex)
        {
            return InvokeAsync<List<(int Index, string Style, string Text)>>(() =>
            {
                var paragraphs = new List<(int Index, string Style, string Text)>();
                dynamic document = null;
                dynamic paragraphCollection = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    if (document == null)
                    {
                        return paragraphs;
                    }

                    paragraphCollection = document.Paragraphs;
                    var paragraphCount = paragraphCollection == null ? 0 : Convert.ToInt32(paragraphCollection.Count);
                    if (paragraphCount <= 0)
                    {
                        return paragraphs;
                    }

                    var start = Math.Max(0, fromIndex);
                    var end = Math.Min(paragraphCount - 1, toIndex);
                    if (end < start)
                    {
                        return paragraphs;
                    }

                    for (var index = start; index <= end; index++)
                    {
                        dynamic paragraph = null;
                        dynamic range = null;
                        try
                        {
                            paragraph = paragraphCollection[index + 1];
                            range = paragraph == null ? null : paragraph.Range;
                            paragraphs.Add((
                                index,
                                ReadParagraphStyleInternal(paragraph),
                                NormalizeParagraphText(range == null ? string.Empty : Convert.ToString(range.Text))));
                        }
                        finally
                        {
                            TryReleaseComObject(range);
                            TryReleaseComObject(paragraph);
                        }
                    }

                    return paragraphs;
                }
                catch
                {
                    return paragraphs;
                }
                finally
                {
                    TryReleaseComObject(paragraphCollection);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<List<(int ParagraphIndex, int CharOffset, string ParagraphText)>> SearchTextAsync(
            string keyword,
            bool useRegex,
            int maxResults)
        {
            return InvokeAsync<List<(int ParagraphIndex, int CharOffset, string ParagraphText)>>(() =>
            {
                var results = new List<(int ParagraphIndex, int CharOffset, string ParagraphText)>();
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    return results;
                }

                dynamic document = null;
                dynamic paragraphCollection = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    if (document == null)
                    {
                        return results;
                    }

                    paragraphCollection = document.Paragraphs;
                    var paragraphCount = paragraphCollection == null ? 0 : Convert.ToInt32(paragraphCollection.Count);
                    var limit = maxResults <= 0 ? 10 : maxResults;
                    Regex regex = null;
                    if (useRegex)
                    {
                        regex = new Regex(keyword, RegexOptions.IgnoreCase);
                    }

                    for (var index = 0; index < paragraphCount && results.Count < limit; index++)
                    {
                        dynamic paragraph = null;
                        dynamic range = null;
                        try
                        {
                            paragraph = paragraphCollection[index + 1];
                            range = paragraph == null ? null : paragraph.Range;
                            var text = NormalizeParagraphText(range == null ? string.Empty : Convert.ToString(range.Text));
                            if (string.IsNullOrWhiteSpace(text))
                            {
                                continue;
                            }

                            var offset = -1;
                            if (useRegex)
                            {
                                var match = regex.Match(text);
                                if (match.Success)
                                {
                                    offset = match.Index;
                                }
                            }
                            else
                            {
                                offset = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                            }

                            if (offset >= 0)
                            {
                                results.Add((index, offset, text));
                            }
                        }
                        finally
                        {
                            TryReleaseComObject(range);
                            TryReleaseComObject(paragraph);
                        }
                    }

                    return results;
                }
                catch
                {
                    return results;
                }
                finally
                {
                    TryReleaseComObject(paragraphCollection);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<string> GetParagraphTextAsync(int paragraphIndex)
        {
            return InvokeAsync<string>(() =>
            {
                dynamic document = null;
                dynamic paragraphs = null;
                dynamic paragraph = null;
                dynamic range = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    if (document == null)
                    {
                        return string.Empty;
                    }

                    paragraphs = document.Paragraphs;
                    var paragraphCount = paragraphs == null ? 0 : Convert.ToInt32(paragraphs.Count);
                    if (paragraphIndex < 0 || paragraphIndex >= paragraphCount)
                    {
                        return string.Empty;
                    }

                    paragraph = paragraphs[paragraphIndex + 1];
                    range = paragraph == null ? null : paragraph.Range;
                    return NormalizeParagraphText(range == null ? string.Empty : Convert.ToString(range.Text));
                }
                catch
                {
                    return string.Empty;
                }
                finally
                {
                    TryReleaseComObject(range);
                    TryReleaseComObject(paragraph);
                    TryReleaseComObject(paragraphs);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<(int TableCount, int ImageCount)> GetDocumentStatsAsync()
        {
            return InvokeAsync<(int TableCount, int ImageCount)>(() =>
            {
                dynamic document = null;
                dynamic tables = null;
                dynamic inlineShapes = null;
                dynamic shapes = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    if (document == null)
                    {
                        return (0, 0);
                    }

                    tables = document.Tables;
                    inlineShapes = document.InlineShapes;
                    shapes = document.Shapes;
                    return (
                        tables == null ? 0 : Convert.ToInt32(tables.Count),
                        (inlineShapes == null ? 0 : Convert.ToInt32(inlineShapes.Count))
                        + (shapes == null ? 0 : Convert.ToInt32(shapes.Count)));
                }
                catch
                {
                    return (0, 0);
                }
                finally
                {
                    TryReleaseComObject(shapes);
                    TryReleaseComObject(inlineShapes);
                    TryReleaseComObject(tables);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task NavigateToParagraphAsync(int paragraphIndex)
        {
            return InvokeAsync(() =>
            {
                dynamic document = null;
                dynamic paragraphs = null;
                dynamic paragraph = null;
                dynamic range = null;
                dynamic selection = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    if (document == null)
                    {
                        return;
                    }

                    paragraphs = document.Paragraphs;
                    var paragraphCount = paragraphs == null ? 0 : Convert.ToInt32(paragraphs.Count);
                    if (paragraphIndex < 0 || paragraphIndex >= paragraphCount)
                    {
                        return;
                    }

                    paragraph = paragraphs[paragraphIndex + 1];
                    range = paragraph == null ? null : paragraph.Range;
                    selection = _wordApplication.Selection;
                    if (selection != null && range != null)
                    {
                        selection.SetRange(range.Start, range.End);
                        selection.Select();
                    }
                }
                finally
                {
                    TryReleaseComObject(selection);
                    TryReleaseComObject(range);
                    TryReleaseComObject(paragraph);
                    TryReleaseComObject(paragraphs);
                    TryReleaseComObject(document);
                }
            });
        }

        public void Dispose()
        {
            _uiThreadInvoker.Dispose();
        }

        private static int SafeConvertToInt(Func<object> accessor)
        {
            try
            {
                var value = accessor();
                return value == null ? 0 : Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static bool SafeConvertToBool(Func<object> accessor)
        {
            try
            {
                var value = accessor();
                return value != null && Convert.ToBoolean(value);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadParagraphStyleInternal(dynamic paragraph)
        {
            dynamic style = null;
            try
            {
                style = paragraph == null ? null : paragraph.get_Style();
                if (style == null)
                {
                    return string.Empty;
                }

                try
                {
                    return Convert.ToString(style.NameLocal);
                }
                catch
                {
                    return Convert.ToString(style);
                }
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                TryReleaseComObject(style);
            }
        }

        private static int GetParagraphIndexFromRangeStartInternal(dynamic document, int rangeStart)
        {
            dynamic paragraphs = null;
            try
            {
                paragraphs = document.Paragraphs;
                var paragraphCount = paragraphs == null ? 0 : Convert.ToInt32(paragraphs.Count);
                for (var index = 1; index <= paragraphCount; index++)
                {
                    dynamic paragraph = null;
                    dynamic paragraphRange = null;
                    try
                    {
                        paragraph = paragraphs[index];
                        paragraphRange = paragraph == null ? null : paragraph.Range;
                        if (paragraphRange != null && SafeConvertToInt(() => paragraphRange.Start) >= rangeStart)
                        {
                            return index - 1;
                        }
                    }
                    finally
                    {
                        TryReleaseComObject(paragraphRange);
                        TryReleaseComObject(paragraph);
                    }
                }

                return Math.Max(0, paragraphCount - 1);
            }
            catch
            {
                return -1;
            }
            finally
            {
                TryReleaseComObject(paragraphs);
            }
        }

        private static string NormalizeParagraphText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text
                .Replace("\r", string.Empty)
                .Replace("\a", string.Empty)
                .Trim();
        }

        private static void TryReleaseComObject(object comObject)
        {
            if (comObject == null)
            {
                return;
            }

            try
            {
                if (Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch
            {
            }
        }
    }
}
