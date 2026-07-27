using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using SmartWord.OfficeIntegration.WordWrappers;
using Word = Microsoft.Office.Interop.Word;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    /// <summary>
    /// 管理单个测试拥有的 Word 应用、临时文档和 COM 生命周期。
    /// </summary>
    internal sealed class WordTestSession : IDisposable
    {
        private readonly Word.Application _wordApplication;
        private readonly OwnedWordProcessGuard _processGuard;
        private bool _disposed;

        private WordTestSession(
            object wordApplication,
            OwnedWordProcessGuard processGuard,
            string workspacePath)
        {
            _wordApplication = (Word.Application)wordApplication;
            _processGuard = processGuard;
            WorkspacePath = workspacePath;
            WordWrapper = new WordApplicationWrapper(wordApplication);
        }

        public WordApplicationWrapper WordWrapper { get; }

        public string WorkspacePath { get; }

        public int OwnedProcessId => _processGuard.ProcessId;

        public static WordTestSession Start(Control dispatcher)
        {
            _ = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            var wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null)
            {
                throw new InvalidOperationException("当前机器未注册 Word.Application COM 组件。");
            }

            object wordApplication = null;
            OwnedWordProcessGuard processGuard = null;
            var processIdsBeforeStart = OwnedWordProcessGuard.SnapshotWordProcessIds();
            try
            {
                wordApplication = Activator.CreateInstance(wordType);
                var app = (Word.Application)wordApplication;
                app.Visible = false;
                app.DisplayAlerts = Word.WdAlertLevel.wdAlertsNone;
                processGuard = OwnedWordProcessGuard.Capture(app, processIdsBeforeStart);

                var workspacePath = Path.Combine(
                    Path.GetTempPath(),
                    "SmartWord",
                    "RealWordTests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workspacePath);
                return new WordTestSession(wordApplication, processGuard, workspacePath);
            }
            catch
            {
                TryQuit(wordApplication);
                ComObjectReleaser.FinalRelease(wordApplication);
                processGuard?.EnsureExited();
                throw;
            }
        }

        public Task<string> CreateBasicFixtureAsync(string fileName = "basic.docx")
        {
            return WordWrapper.InvokeAsync<string>(() =>
            {
                var path = Path.Combine(WorkspacePath, fileName);
                Word.Documents documents = null;
                Word.Document document = null;
                Word.Range content = null;
                try
                {
                    documents = _wordApplication.Documents;
                    document = documents.Add();
                    content = document.Content;
                    content.Text = "基线标题\r第一段内容\r第二段内容\r待删除段落\r";
                    document.SaveAs2(path, Word.WdSaveFormat.wdFormatDocumentDefault);
                    document.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
                    return path;
                }
                finally
                {
                    ComObjectReleaser.FinalRelease(content);
                    ComObjectReleaser.FinalRelease(document);
                    ComObjectReleaser.FinalRelease(documents);
                }
            });
        }

        public Task<string> CreateTableFixtureAsync(string fileName = "table.docx")
        {
            return WordWrapper.InvokeAsync(() =>
            {
                var path = Path.Combine(WorkspacePath, fileName);
                Word.Documents documents = null;
                Word.Document document = null;
                Word.Range content = null;
                Word.Range range = null;
                Word.Tables tables = null;
                Word.Table table = null;
                try
                {
                    documents = _wordApplication.Documents;
                    document = documents.Add();
                    content = document.Content;
                    content.Text = "表格测试文档\r";
                    range = document.Range(document.Content.End - 1, document.Content.End - 1);
                    tables = document.Tables;
                    table = tables.Add(range, 2, 2);
                    SetCellText(table, 1, 1, "A1");
                    SetCellText(table, 1, 2, "B1");
                    SetCellText(table, 2, 1, "A2");
                    SetCellText(table, 2, 2, "B2");
                    document.SaveAs2(path, Word.WdSaveFormat.wdFormatDocumentDefault);
                    document.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
                    return path;
                }
                finally
                {
                    ComObjectReleaser.FinalRelease(table);
                    ComObjectReleaser.FinalRelease(tables);
                    ComObjectReleaser.FinalRelease(range);
                    ComObjectReleaser.FinalRelease(content);
                    ComObjectReleaser.FinalRelease(document);
                    ComObjectReleaser.FinalRelease(documents);
                }
            });
        }

        public Task<string> CreateHeaderFooterFixtureAsync(string fileName = "header-footer.docx")
        {
            return WordWrapper.InvokeAsync(() =>
            {
                var path = Path.Combine(WorkspacePath, fileName);
                Word.Documents documents = null;
                Word.Document document = null;
                Word.Range content = null;
                Word.Sections sections = null;
                Word.Section section = null;
                Word.HeadersFooters headers = null;
                Word.HeadersFooters footers = null;
                Word.HeaderFooter header = null;
                Word.HeaderFooter footer = null;
                Word.Range headerRange = null;
                Word.Range footerRange = null;
                try
                {
                    documents = _wordApplication.Documents;
                    document = documents.Add();
                    content = document.Content;
                    content.Text = "页眉页脚测试正文\r";
                    sections = document.Sections;
                    section = sections[1];
                    headers = section.Headers;
                    footers = section.Footers;
                    header = headers[Word.WdHeaderFooterIndex.wdHeaderFooterPrimary];
                    footer = footers[Word.WdHeaderFooterIndex.wdHeaderFooterPrimary];
                    headerRange = header.Range;
                    footerRange = footer.Range;
                    headerRange.Text = "原始页眉";
                    footerRange.Text = "原始页脚";
                    document.SaveAs2(path, Word.WdSaveFormat.wdFormatDocumentDefault);
                    document.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
                    return path;
                }
                finally
                {
                    ComObjectReleaser.FinalRelease(footerRange);
                    ComObjectReleaser.FinalRelease(headerRange);
                    ComObjectReleaser.FinalRelease(footer);
                    ComObjectReleaser.FinalRelease(header);
                    ComObjectReleaser.FinalRelease(footers);
                    ComObjectReleaser.FinalRelease(headers);
                    ComObjectReleaser.FinalRelease(section);
                    ComObjectReleaser.FinalRelease(sections);
                    ComObjectReleaser.FinalRelease(content);
                    ComObjectReleaser.FinalRelease(document);
                    ComObjectReleaser.FinalRelease(documents);
                }
            });
        }

        public Task OpenDocumentAsync(string path, bool readOnly = false)
        {
            return WordWrapper.InvokeAsync(() =>
            {
                Word.Documents documents = null;
                Word.Document document = null;
                try
                {
                    documents = _wordApplication.Documents;
                    document = documents.Open(path, ReadOnly: readOnly, Visible: false);
                    document.Activate();
                }
                finally
                {
                    ComObjectReleaser.FinalRelease(document);
                    ComObjectReleaser.FinalRelease(documents);
                }
            });
        }

        public Task ActivateDocumentAsync(string fullPath)
        {
            return WordWrapper.InvokeAsync(() =>
            {
                Word.Documents documents = null;
                try
                {
                    documents = _wordApplication.Documents;
                    var count = Convert.ToInt32(documents.Count);
                    for (var index = 1; index <= count; index++)
                    {
                        Word.Document document = null;
                        try
                        {
                            document = documents[index];
                            if (string.Equals(
                                Convert.ToString(document.FullName),
                                fullPath,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                document.Activate();
                                return;
                            }
                        }
                        finally
                        {
                            ComObjectReleaser.FinalRelease(document);
                        }
                    }

                    throw new InvalidOperationException("未找到待激活的测试文档：" + fullPath);
                }
                finally
                {
                    ComObjectReleaser.FinalRelease(documents);
                }
            });
        }

        public Task ProtectActiveDocumentAsync(string password = "SmartWord-Test")
        {
            return WordWrapper.InvokeAsync(() =>
            {
                Word.Document document = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    document.Protect(Word.WdProtectionType.wdAllowOnlyFormFields, true, password);
                }
                finally
                {
                    ComObjectReleaser.FinalRelease(document);
                }
            });
        }

        public Task SetSelectionAsync(int start, int end)
        {
            return WordWrapper.InvokeAsync(() =>
            {
                Word.Selection selection = null;
                try
                {
                    selection = _wordApplication.Selection;
                    selection.SetRange(start, end);
                }
                finally
                {
                    ComObjectReleaser.FinalRelease(selection);
                }
            });
        }

        public Task<string> ReadActiveDocumentTextAsync()
        {
            return WordWrapper.InvokeAsync<string>(() =>
            {
                Word.Document document = null;
                Word.Range content = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    content = document.Content;
                    return Convert.ToString(content.Text) ?? string.Empty;
                }
                finally
                {
                    ComObjectReleaser.FinalRelease(content);
                    ComObjectReleaser.FinalRelease(document);
                }
            });
        }

        public Task SaveActiveDocumentAsync()
        {
            return WordWrapper.InvokeAsync(() =>
            {
                Word.Document document = null;
                try
                {
                    document = _wordApplication.ActiveDocument;
                    document.Save();
                }
                finally
                {
                    ComObjectReleaser.FinalRelease(document);
                }
            });
        }

        public Task<string> GetBuiltInStyleNameAsync(Word.WdBuiltinStyle style)
        {
            return WordWrapper.InvokeAsync<string>(() =>
            {
                Word.Style wordStyle = null;
                try
                {
                    wordStyle = _wordApplication.ActiveDocument.Styles[style];
                    return wordStyle.NameLocal;
                }
                finally
                {
                    ComObjectReleaser.FinalRelease(wordStyle);
                }
            });
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Exception failure = null;
            try
            {
                CloseAllDocuments();
                _wordApplication.Quit(Word.WdSaveOptions.wdDoNotSaveChanges);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                WordWrapper.Dispose();
                ComObjectReleaser.FinalRelease((object)_wordApplication);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                _processGuard.EnsureExited();
                TryDeleteWorkspace();
            }

            if (failure != null)
            {
                throw new InvalidOperationException("关闭测试 Word 会话失败。", failure);
            }
        }

        private static void SetCellText(Word.Table table, int row, int column, string text)
        {
            Word.Cell cell = null;
            Word.Range range = null;
            try
            {
                cell = table.Cell(row, column);
                range = cell.Range;
                range.Text = text;
            }
            finally
            {
                ComObjectReleaser.FinalRelease(range);
                ComObjectReleaser.FinalRelease(cell);
            }
        }

        private void CloseAllDocuments()
        {
            Word.Documents documents = null;
            try
            {
                documents = _wordApplication.Documents;
                for (var index = Convert.ToInt32(documents.Count); index >= 1; index--)
                {
                    Word.Document document = null;
                    try
                    {
                        document = documents[index];
                        document.Close(Word.WdSaveOptions.wdDoNotSaveChanges);
                    }
                    catch
                    {
                        // 单个文档关闭失败时继续退出测试拥有的 Word 进程。
                    }
                    finally
                    {
                        ComObjectReleaser.FinalRelease(document);
                    }
                }
            }
            finally
            {
                ComObjectReleaser.FinalRelease(documents);
            }
        }

        private void TryDeleteWorkspace()
        {
            try
            {
                if (Directory.Exists(WorkspacePath))
                {
                    Directory.Delete(WorkspacePath, true);
                }
            }
            catch
            {
                // 临时目录清理失败不应触发对用户文件的扩大删除。
            }
        }

        private static void TryQuit(object wordApplication)
        {
            if (wordApplication == null)
            {
                return;
            }

            try
            {
                ((dynamic)wordApplication).Quit(false);
            }
            catch
            {
            }
        }
    }
}
