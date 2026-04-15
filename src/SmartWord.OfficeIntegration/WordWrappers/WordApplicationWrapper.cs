using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Serilog;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.Models;
using SmartWord.OfficeIntegration.Reading;

namespace SmartWord.OfficeIntegration.WordWrappers
{
    /// <summary>
    /// 负责把对 Word 的访问切回宿主 UI 线程执行。
    /// </summary>
    public sealed class WordApplicationWrapper : IDisposable, IUndoScopeFactory
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
                var initialDocumentPath = string.Empty;
                dynamic activeDocument = null;
                try
                {
                    activeDocument = _wordApplication.ActiveDocument;
                    initialDocumentPath = activeDocument == null ? string.Empty : Convert.ToString(activeDocument.FullName);
                }
                catch
                {
                    initialDocumentPath = string.Empty;
                }
                finally
                {
                    TryReleaseComObject(activeDocument);
                }

                var undoScope = new UndoRecordWrapper(_wordApplication, this, initialDocumentPath);
                undoScope.BeginTransaction(operationName);
                return undoScope;
            });
        }

        async System.Threading.Tasks.Task<IUndoScope> IUndoScopeFactory.BeginTaskUndoAsync(
            string operationName,
            System.Threading.CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await BeginTaskUndoAsync(operationName).ConfigureAwait(false);
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
                catch (Exception ex)
                {
                    WriteDiagnosticWarning("读取光标所在段落失败：" + ex);
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

        public async Task<(bool HasSelection, string Text, int ParagraphIndex, int CharStart, int CharEnd)> GetSelectionInfoAsync()
        {
            var snapshot = await GetSelectionSnapshotAsync().ConfigureAwait(false);
            return (
                snapshot.HasSelection,
                snapshot.Text,
                snapshot.ParagraphIndex,
                snapshot.CharStart,
                snapshot.CharEnd);
        }

        public Task<SelectionSnapshot> GetSelectionSnapshotAsync()
        {
            return InvokeAsync(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                dynamic document = null;
                dynamic selection = null;
                dynamic range = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    selection = _wordApplication.Selection;
                    range = selection == null ? null : selection.Range;
                    if (document == null || range == null)
                    {
                        return new SelectionSnapshot();
                    }

                    var rangeStart = SafeConvertToInt(() => range.Start);
                    var rangeEnd = SafeConvertToInt(() => range.End);
                    var selectionText = NormalizeParagraphText(Convert.ToString(range.Text));
                    var hasSelection = rangeStart != rangeEnd
                        && !string.IsNullOrWhiteSpace(selectionText);

                    var paragraphRanges = GetParagraphRangeBoundsInternal((object)document);
                    var startParagraphIndex = ParagraphRangeLocator.LocateParagraphIndex(paragraphRanges, rangeStart);
                    var endAnchorPosition = hasSelection
                        ? Math.Max(rangeStart, rangeEnd - 1)
                        : rangeStart;
                    var endParagraphIndex = ParagraphRangeLocator.LocateParagraphIndex(paragraphRanges, endAnchorPosition);
                    var paragraphIndex = startParagraphIndex >= 0 ? startParagraphIndex : endParagraphIndex;
                    var currentParagraph = paragraphRanges.FirstOrDefault(item => item.Index == paragraphIndex);
                    var charStart = currentParagraph == null
                        ? -1
                        : Math.Max(0, rangeStart - currentParagraph.Start);
                    var charEnd = currentParagraph == null
                        ? -1
                        : Math.Max(
                            charStart,
                            Math.Min(Math.Max(currentParagraph.Start, currentParagraph.End), endAnchorPosition) - currentParagraph.Start);

                    var snapshot = new SelectionSnapshot
                    {
                        HasSelection = hasSelection,
                        Text = selectionText,
                        ParagraphIndex = paragraphIndex,
                        StartParagraphIndex = startParagraphIndex,
                        EndParagraphIndex = endParagraphIndex,
                        IsMultiParagraph = hasSelection && startParagraphIndex >= 0 && endParagraphIndex > startParagraphIndex,
                        CharStart = charStart,
                        CharEnd = charEnd
                    };

                    WriteDiagnosticInfo(
                        "读取选区信息成功。ParagraphIndex="
                        + snapshot.ParagraphIndex
                        + ", HasSelection="
                        + snapshot.HasSelection
                        + ", IsMultiParagraph="
                        + snapshot.IsMultiParagraph
                        + ", DurationMs="
                        + stopwatch.ElapsedMilliseconds);
                    return snapshot;
                }
                catch (Exception ex)
                {
                    WriteDiagnosticWarning("读取选区信息失败：" + ex);
                    return new SelectionSnapshot();
                }
                finally
                {
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
                var stopwatch = Stopwatch.StartNew();
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

                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticInfo(
                        "读取文档标题成功。DocumentPath="
                        + documentPath
                        + ", HeadingCount="
                        + headings.Count
                        + ", DurationMs="
                        + stopwatch.ElapsedMilliseconds);
                    return headings;
                }
                catch (Exception ex)
                {
                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticWarning("读取文档标题失败。DocumentPath=" + documentPath + "，Exception=" + ex);
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
                var stopwatch = Stopwatch.StartNew();
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

                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticInfo(
                        "读取段落区间成功。DocumentPath="
                        + documentPath
                        + ", FromIndex="
                        + fromIndex
                        + ", ToIndex="
                        + toIndex
                        + ", Count="
                        + paragraphs.Count
                        + ", DurationMs="
                        + stopwatch.ElapsedMilliseconds);
                    return paragraphs;
                }
                catch (Exception ex)
                {
                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticWarning(
                        "读取段落区间失败。DocumentPath="
                        + documentPath
                        + ", FromIndex="
                        + fromIndex
                        + ", ToIndex="
                        + toIndex
                        + ", Exception="
                        + ex);
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
                var stopwatch = Stopwatch.StartNew();
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

                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticInfo(
                        "搜索文本成功。DocumentPath="
                        + documentPath
                        + ", Keyword="
                        + keyword
                        + ", UseRegex="
                        + useRegex
                        + ", ResultCount="
                        + results.Count
                        + ", DurationMs="
                        + stopwatch.ElapsedMilliseconds);
                    return results;
                }
                catch (Exception ex)
                {
                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticWarning(
                        "搜索文本失败。DocumentPath="
                        + documentPath
                        + ", Keyword="
                        + keyword
                        + ", UseRegex="
                        + useRegex
                        + ", Exception="
                        + ex);
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

        public Task<string> GetParagraphStyleAsync(int paragraphIndex)
        {
            return InvokeAsync<string>(() =>
            {
                dynamic document = null;
                dynamic paragraphs = null;
                dynamic paragraph = null;
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
                    return ReadParagraphStyleInternal(paragraph);
                }
                catch
                {
                    return string.Empty;
                }
                finally
                {
                    TryReleaseComObject(paragraph);
                    TryReleaseComObject(paragraphs);
                    TryReleaseComObject(document);
                }
            });
        }

        public async Task<bool> ParagraphExistsAsync(int paragraphIndex)
        {
            if (paragraphIndex < 0)
            {
                return false;
            }

            var paragraphCount = await GetParagraphCountAsync().ConfigureAwait(false);
            return paragraphIndex < paragraphCount;
        }

        public Task<T> InvokeWithActiveDocumentAsync<T>(Func<object, object, T> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return InvokeAsync(() =>
            {
                dynamic activeDocument = null;
                try
                {
                    activeDocument = _wordApplication.ActiveDocument;
                    return action((object)_wordApplication, (object)activeDocument);
                }
                finally
                {
                    if (activeDocument != null)
                    {
                        TryReleaseComObject(activeDocument);
                    }
                }
            });
        }

        public Task<DocumentStructureStats> GetDocumentStatsAsync()
        {
            return InvokeAsync(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                dynamic document = null;
                dynamic tables = null;
                dynamic inlineShapes = null;
                dynamic shapes = null;
                dynamic comments = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    if (document == null)
                    {
                        return new DocumentStructureStats();
                    }

                    tables = document.Tables;
                    inlineShapes = document.InlineShapes;
                    shapes = document.Shapes;
                    comments = document.Comments;
                    var stats = new DocumentStructureStats
                    {
                        TableCount = tables == null ? 0 : Convert.ToInt32(tables.Count),
                        ImageCount = (inlineShapes == null ? 0 : Convert.ToInt32(inlineShapes.Count))
                            + (shapes == null ? 0 : Convert.ToInt32(shapes.Count)),
                        AnnotationCount = comments == null ? 0 : Convert.ToInt32(comments.Count)
                    };
                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticInfo(
                        "读取文档统计成功。DocumentPath="
                        + documentPath
                        + ", TableCount="
                        + stats.TableCount
                        + ", ImageCount="
                        + stats.ImageCount
                        + ", AnnotationCount="
                        + stats.AnnotationCount
                        + ", DurationMs="
                        + stopwatch.ElapsedMilliseconds);
                    return stats;
                }
                catch (Exception ex)
                {
                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticWarning("读取文档统计失败。DocumentPath=" + documentPath + "，Exception=" + ex);
                    return new DocumentStructureStats();
                }
                finally
                {
                    TryReleaseComObject(comments);
                    TryReleaseComObject(shapes);
                    TryReleaseComObject(inlineShapes);
                    TryReleaseComObject(tables);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<TableReadResult> ReadTableAsync(int tableIndex, int maxRows, int maxColumns)
        {
            return InvokeAsync<TableReadResult>(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                var diagnostics = new ReadDiagnostics();
                dynamic document = null;
                dynamic tables = null;
                dynamic table = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    if (document == null)
                    {
                        return CreateTableReadFailure("当前没有活动文档，无法读取表格。", diagnostics);
                    }

                    tables = document.Tables;
                    var tableCount = tables == null ? 0 : Convert.ToInt32(tables.Count);
                    if (tableIndex < 0 || tableIndex >= tableCount)
                    {
                        return CreateTableReadFailure(
                            "指定的表格索引超出范围。当前文档共有 " + tableCount + " 个表格。",
                            diagnostics);
                    }

                    table = tables.Item(tableIndex + 1);
                    if (table == null)
                    {
                        return CreateTableReadFailure("无法访问指定的表格对象。", diagnostics);
                    }

                    TableSnapshot flattenedSnapshot;
                    if (TryBuildTableSnapshotFromFlattenedCells(
                        document,
                        table,
                        tableIndex,
                        maxRows,
                        maxColumns,
                        diagnostics,
                        out flattenedSnapshot))
                    {
                        return CreateTableReadSuccess(flattenedSnapshot, diagnostics, document, stopwatch);
                    }

                    diagnostics.AddWarning("已回退到逐行读取表格路径。");
                    TableSnapshot rowSnapshot;
                    if (TryBuildTableSnapshotFromRows(
                        document,
                        table,
                        tableIndex,
                        maxRows,
                        maxColumns,
                        diagnostics,
                        out rowSnapshot))
                    {
                        return CreateTableReadSuccess(rowSnapshot, diagnostics, document, stopwatch);
                    }

                    var failureReason = diagnostics.HasWarnings
                        ? "读取表格失败：" + string.Join("；", diagnostics.Warnings)
                        : "读取表格失败，未能解析表格结构。";
                    WriteDiagnosticWarning(
                        "读取表格失败。DocumentPath="
                        + SafeGetDocumentPath(document)
                        + ", TableIndex="
                        + tableIndex
                        + ", Reason="
                        + failureReason);
                    return CreateTableReadFailure(failureReason, diagnostics);
                }
                catch (Exception ex)
                {
                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticWarning(
                        "读取表格失败。DocumentPath="
                        + documentPath
                        + ", TableIndex="
                        + tableIndex
                        + ", Exception="
                        + ex);
                    diagnostics.AddWarning("读取表格时发生异常：" + ex.Message);
                    return CreateTableReadFailure("读取表格时发生异常：" + ex.Message, diagnostics);
                }
                finally
                {
                    TryReleaseComObject(table);
                    TryReleaseComObject(tables);
                    TryReleaseComObject(document);
                }
            });
        }

        public Task<IReadOnlyList<AnnotationSnapshot>> ReadAnnotationsAsync(string authorFilter, int maxResults)
        {
            return InvokeAsync<IReadOnlyList<AnnotationSnapshot>>(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                var annotations = new List<AnnotationSnapshot>();
                dynamic document = null;
                dynamic comments = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    if (document == null)
                    {
                        return annotations;
                    }

                    comments = document.Comments;
                    var commentCount = comments == null ? 0 : Convert.ToInt32(comments.Count);
                    for (var index = 1; index <= commentCount; index++)
                    {
                        if (annotations.Count >= Math.Max(1, maxResults))
                        {
                            break;
                        }

                        dynamic comment = null;
                        dynamic scope = null;
                        dynamic scopeParagraphs = null;
                        dynamic paragraph = null;
                        dynamic paragraphRange = null;
                        try
                        {
                            comment = comments[index];
                            var author = Convert.ToString(comment.Author) ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(authorFilter)
                                && author.IndexOf(authorFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }

                            scope = comment.Scope;
                            scopeParagraphs = scope == null ? null : scope.Paragraphs;
                            paragraph = scopeParagraphs == null ? null : scopeParagraphs[1];
                            paragraphRange = paragraph == null ? null : paragraph.Range;

                            annotations.Add(new AnnotationSnapshot
                            {
                                AnnotationIndex = index - 1,
                                Author = author,
                                CreatedAt = SafeReadString(() => Convert.ToDateTime(comment.Date).ToString("yyyy-MM-dd HH:mm:ss")),
                                Text = NormalizeParagraphText(Convert.ToString(comment.Range == null ? string.Empty : comment.Range.Text)),
                                AnchorText = NormalizeParagraphText(scope == null ? string.Empty : Convert.ToString(scope.Text)),
                                ParagraphIndex = paragraphRange == null
                                    ? -1
                                    : GetParagraphIndexFromRangeStartInternal(document, SafeConvertToInt(() => paragraphRange.Start))
                            });
                        }
                        finally
                        {
                            TryReleaseComObject(paragraphRange);
                            TryReleaseComObject(paragraph);
                            TryReleaseComObject(scopeParagraphs);
                            TryReleaseComObject(scope);
                            TryReleaseComObject(comment);
                        }
                    }

                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticInfo(
                        "读取批注成功。DocumentPath="
                        + documentPath
                        + ", AuthorFilter="
                        + (authorFilter ?? string.Empty)
                        + ", Count="
                        + annotations.Count
                        + ", DurationMs="
                        + stopwatch.ElapsedMilliseconds);
                    return annotations;
                }
                catch (Exception ex)
                {
                    var documentPath = SafeGetDocumentPath(document);
                    WriteDiagnosticWarning(
                        "读取批注失败。DocumentPath="
                        + documentPath
                        + ", AuthorFilter="
                        + (authorFilter ?? string.Empty)
                        + ", Exception="
                        + ex);
                    return annotations;
                }
                finally
                {
                    TryReleaseComObject(comments);
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

        private static bool TryConvertToInt(Func<object> accessor, out int value, out string errorMessage)
        {
            try
            {
                var rawValue = accessor();
                value = rawValue == null ? 0 : Convert.ToInt32(rawValue);
                errorMessage = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                value = 0;
                errorMessage = ex.Message;
                return false;
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
            try
            {
                var paragraphRanges = GetParagraphRangeBoundsInternal((object)document);
                return ParagraphRangeLocator.LocateParagraphIndex(paragraphRanges, rangeStart);
            }
            catch (Exception ex)
            {
                WriteDiagnosticWarning("根据 Range.Start 定位段落失败。RangeStart=" + rangeStart + "，Exception=" + ex);
                return -1;
            }
        }

        private static List<ParagraphRangeBounds> GetParagraphRangeBoundsInternal(object documentObject)
        {
            var paragraphRanges = new List<ParagraphRangeBounds>();
            dynamic document = documentObject;
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
                        if (paragraphRange == null)
                        {
                            continue;
                        }

                        paragraphRanges.Add(new ParagraphRangeBounds
                        {
                            Index = index - 1,
                            Start = SafeConvertToInt(() => paragraphRange.Start),
                            End = SafeConvertToInt(() => paragraphRange.End)
                        });
                    }
                    finally
                    {
                        TryReleaseComObject(paragraphRange);
                        TryReleaseComObject(paragraph);
                    }
                }

                return paragraphRanges;
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

        private static string SafeGetDocumentPath(dynamic document)
        {
            try
            {
                return document == null ? string.Empty : Convert.ToString(document.FullName);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeReadString(Func<string> accessor)
        {
            try
            {
                return accessor() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void WriteDiagnosticInfo(string message)
        {
            Log.Information("{Message}", message ?? string.Empty);
        }

        private static void WriteDiagnosticWarning(string message)
        {
            Log.Warning("{Message}", message ?? string.Empty);
        }

        private static bool TryBuildTableSnapshotFromFlattenedCells(
            dynamic document,
            dynamic table,
            int tableIndex,
            int maxRows,
            int maxColumns,
            ReadDiagnostics diagnostics,
            out TableSnapshot snapshot)
        {
            snapshot = null;
            dynamic tableRange = null;
            dynamic cells = null;
            try
            {
                tableRange = table.Range;
                if (tableRange == null)
                {
                    diagnostics.AddWarning("无法访问表格范围。");
                    return false;
                }

                cells = tableRange.Cells;
                if (cells == null)
                {
                    diagnostics.AddWarning("无法访问表格单元格集合。");
                    return false;
                }

                if (!TryConvertToInt(() => cells.Count, out var cellCount, out var countError))
                {
                    diagnostics.AddWarning("无法读取表格单元格数量：" + countError);
                    return false;
                }

                if (cellCount <= 0)
                {
                    diagnostics.AddWarning("表格单元格数量为 0。");
                    return false;
                }

                var matrix = new SortedDictionary<int, SortedDictionary<int, string>>();
                var skippedCellCount = 0;
                for (var cellIndex = 1; cellIndex <= cellCount; cellIndex++)
                {
                    dynamic cell = null;
                    dynamic cellRange = null;
                    try
                    {
                        cell = cells.Item(cellIndex);
                        cellRange = cell == null ? null : cell.Range;
                        if (!TryConvertToInt(() => cell.RowIndex, out var rowIndex, out var rowError))
                        {
                            skippedCellCount++;
                            diagnostics.AddWarning("无法读取单元格行索引：" + rowError);
                            continue;
                        }

                        if (!TryConvertToInt(() => cell.ColumnIndex, out var columnIndex, out var columnError))
                        {
                            skippedCellCount++;
                            diagnostics.AddWarning("无法读取单元格列索引：" + columnError);
                            continue;
                        }

                        var zeroBasedRowIndex = Math.Max(0, rowIndex - 1);
                        var zeroBasedColumnIndex = Math.Max(0, columnIndex - 1);
                        if (!matrix.TryGetValue(zeroBasedRowIndex, out var rowMap))
                        {
                            rowMap = new SortedDictionary<int, string>();
                            matrix[zeroBasedRowIndex] = rowMap;
                        }

                        rowMap[zeroBasedColumnIndex] = NormalizeParagraphText(
                            cellRange == null ? string.Empty : Convert.ToString(cellRange.Text));
                    }
                    catch (Exception ex)
                    {
                        skippedCellCount++;
                        WriteDiagnosticWarning(
                            "按扁平单元格读取表格失败。TableIndex="
                            + tableIndex
                            + ", CellSequence="
                            + (cellIndex - 1)
                            + ", Exception="
                            + ex);
                    }
                    finally
                    {
                        TryReleaseComObject(cellRange);
                        TryReleaseComObject(cell);
                    }
                }

                if (matrix.Count <= 0)
                {
                    diagnostics.AddWarning("扁平单元格路径未解析出任何有效单元格。");
                    return false;
                }

                if (skippedCellCount > 0)
                {
                    diagnostics.IsPartial = true;
                    diagnostics.AddWarning("扁平单元格路径跳过了 " + skippedCellCount + " 个异常单元格。");
                }

                var anchorParagraphIndex = GetParagraphIndexFromRangeStartInternal(
                    document,
                    SafeConvertToInt(() => tableRange.Start));
                snapshot = BuildTableSnapshotFromMatrix(
                    tableIndex,
                    anchorParagraphIndex,
                    maxRows,
                    maxColumns,
                    matrix);
                return true;
            }
            catch (Exception ex)
            {
                diagnostics.AddWarning("扁平单元格路径读取失败：" + ex.Message);
                return false;
            }
            finally
            {
                TryReleaseComObject(cells);
                TryReleaseComObject(tableRange);
            }
        }

        private static bool TryBuildTableSnapshotFromRows(
            dynamic document,
            dynamic table,
            int tableIndex,
            int maxRows,
            int maxColumns,
            ReadDiagnostics diagnostics,
            out TableSnapshot snapshot)
        {
            snapshot = null;
            dynamic rows = null;
            dynamic tableRange = null;
            try
            {
                rows = table.Rows;
                if (rows == null)
                {
                    diagnostics.AddWarning("无法访问表格行集合。");
                    return false;
                }

                if (!TryConvertToInt(() => rows.Count, out var rowCount, out var rowCountError))
                {
                    diagnostics.AddWarning("无法读取表格行数：" + rowCountError);
                    return false;
                }

                if (rowCount <= 0)
                {
                    diagnostics.AddWarning("表格行数为 0。");
                    return false;
                }

                var matrix = new SortedDictionary<int, SortedDictionary<int, string>>();
                var skippedCellCount = 0;
                for (var rowIndex = 1; rowIndex <= rowCount; rowIndex++)
                {
                    dynamic row = null;
                    dynamic rowCells = null;
                    try
                    {
                        row = rows[rowIndex];
                        rowCells = row == null ? null : row.Cells;
                        if (rowCells == null)
                        {
                            continue;
                        }

                        if (!TryConvertToInt(() => rowCells.Count, out var rowCellCount, out var rowCellCountError))
                        {
                            diagnostics.AddWarning(
                                "无法读取第 " + (rowIndex - 1) + " 行的单元格数量：" + rowCellCountError);
                            continue;
                        }

                        var zeroBasedRowIndex = rowIndex - 1;
                        if (!matrix.TryGetValue(zeroBasedRowIndex, out var rowMap))
                        {
                            rowMap = new SortedDictionary<int, string>();
                            matrix[zeroBasedRowIndex] = rowMap;
                        }

                        for (var cellIndex = 1; cellIndex <= rowCellCount; cellIndex++)
                        {
                            dynamic cell = null;
                            dynamic cellRange = null;
                            try
                            {
                                cell = rowCells[cellIndex];
                                cellRange = cell == null ? null : cell.Range;
                                rowMap[cellIndex - 1] = NormalizeParagraphText(
                                    cellRange == null ? string.Empty : Convert.ToString(cellRange.Text));
                            }
                            catch (Exception ex)
                            {
                                skippedCellCount++;
                                WriteDiagnosticWarning(
                                    "按行读取表格失败。TableIndex="
                                    + tableIndex
                                    + ", RowIndex="
                                    + (rowIndex - 1)
                                    + ", CellIndex="
                                    + (cellIndex - 1)
                                    + ", Exception="
                                    + ex);
                            }
                            finally
                            {
                                TryReleaseComObject(cellRange);
                                TryReleaseComObject(cell);
                            }
                        }
                    }
                    finally
                    {
                        TryReleaseComObject(rowCells);
                        TryReleaseComObject(row);
                    }
                }

                if (matrix.Count <= 0)
                {
                    diagnostics.AddWarning("逐行路径未解析出任何有效单元格。");
                    return false;
                }

                if (skippedCellCount > 0)
                {
                    diagnostics.IsPartial = true;
                    diagnostics.AddWarning("逐行路径跳过了 " + skippedCellCount + " 个异常单元格。");
                }

                tableRange = table.Range;
                var anchorParagraphIndex = tableRange == null
                    ? -1
                    : GetParagraphIndexFromRangeStartInternal(document, SafeConvertToInt(() => tableRange.Start));
                snapshot = BuildTableSnapshotFromMatrix(
                    tableIndex,
                    anchorParagraphIndex,
                    maxRows,
                    maxColumns,
                    matrix);
                return true;
            }
            catch (Exception ex)
            {
                diagnostics.AddWarning("逐行路径读取失败：" + ex.Message);
                return false;
            }
            finally
            {
                TryReleaseComObject(tableRange);
                TryReleaseComObject(rows);
            }
        }

        private static TableSnapshot BuildTableSnapshotFromMatrix(
            int tableIndex,
            int anchorParagraphIndex,
            int maxRows,
            int maxColumns,
            SortedDictionary<int, SortedDictionary<int, string>> matrix)
        {
            var detectedMaxRowIndex = matrix.Count <= 0 ? -1 : matrix.Keys.Max();
            var detectedMaxColumnIndex = matrix.Count <= 0
                ? -1
                : matrix.Values
                    .Where(row => row != null && row.Count > 0)
                    .Select(row => row.Keys.Max())
                    .DefaultIfEmpty(-1)
                    .Max();
            var rowCount = detectedMaxRowIndex + 1;
            var columnCount = detectedMaxColumnIndex + 1;
            var snapshot = new TableSnapshot
            {
                TableIndex = tableIndex,
                AnchorParagraphIndex = anchorParagraphIndex,
                RowCount = Math.Max(0, rowCount),
                ColumnCount = Math.Max(0, columnCount),
                RowsTruncated = rowCount > maxRows,
                ColumnsTruncated = columnCount > maxColumns
            };

            var safeMaxRows = Math.Max(1, maxRows);
            var safeMaxColumns = Math.Max(1, maxColumns);
            foreach (var rowEntry in matrix)
            {
                if (rowEntry.Key >= safeMaxRows)
                {
                    break;
                }

                var rowSnapshot = new TableRowSnapshot
                {
                    RowIndex = rowEntry.Key
                };

                foreach (var columnEntry in rowEntry.Value)
                {
                    if (columnEntry.Key >= safeMaxColumns)
                    {
                        break;
                    }

                    rowSnapshot.Cells.Add(new TableCellSnapshot
                    {
                        ColumnIndex = columnEntry.Key,
                        Text = columnEntry.Value
                    });
                }

                snapshot.Rows.Add(rowSnapshot);
            }

            return snapshot;
        }

        private static TableReadResult CreateTableReadFailure(string reason, ReadDiagnostics diagnostics)
        {
            return new TableReadResult
            {
                Success = false,
                FailureReason = string.IsNullOrWhiteSpace(reason) ? "读取表格失败。" : reason,
                Diagnostics = diagnostics ?? new ReadDiagnostics()
            };
        }

        private static TableReadResult CreateTableReadSuccess(
            TableSnapshot snapshot,
            ReadDiagnostics diagnostics,
            dynamic document,
            Stopwatch stopwatch)
        {
            var documentPath = SafeGetDocumentPath(document);
            WriteDiagnosticInfo(
                "读取表格成功。DocumentPath="
                + documentPath
                + ", TableIndex="
                + snapshot.TableIndex
                + ", RowCount="
                + snapshot.RowCount
                + ", ColumnCount="
                + snapshot.ColumnCount
                + ", DurationMs="
                + stopwatch.ElapsedMilliseconds);
            return new TableReadResult
            {
                Success = true,
                Snapshot = snapshot,
                Diagnostics = diagnostics ?? new ReadDiagnostics()
            };
        }

        internal static void TryReleaseComObjectSilently(object comObject)
        {
            TryReleaseComObject(comObject);
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
