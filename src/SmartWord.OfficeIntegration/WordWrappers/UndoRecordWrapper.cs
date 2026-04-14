using System;
using Serilog;
using SmartWord.Core.Interfaces;

namespace SmartWord.OfficeIntegration.WordWrappers
{
    /// <summary>
    /// 提供任务级 UndoRecord 包装，并在回滚时做文档一致性保护。
    /// </summary>
    public sealed class UndoRecordWrapper : IUndoScope
    {
        private readonly dynamic _wordApplication;
        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly string _initialDocumentPath;

        private bool _recordClosed;
        private bool _disposed;

        public UndoRecordWrapper(
            object wordApplication,
            WordApplicationWrapper wordApplicationWrapper,
            string initialDocumentPath)
        {
            _wordApplication = wordApplication;
            _wordApplicationWrapper = wordApplicationWrapper ?? throw new ArgumentNullException(nameof(wordApplicationWrapper));
            _initialDocumentPath = initialDocumentPath ?? string.Empty;
        }

        public void BeginTransaction(string operationName)
        {
            try
            {
                var undoRecord = _wordApplication == null ? null : _wordApplication.UndoRecord;
                if (undoRecord != null)
                {
                    undoRecord.StartCustomRecord(operationName);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "启动 Word UndoRecord 失败。OperationName={OperationName}", operationName);
            }
        }

        public void Commit()
        {
            ExecuteOnUiThread(() =>
            {
                CloseRecord();
                Log.Information("任务级 UndoRecord 已提交。DocumentPath={DocumentPath}", _initialDocumentPath);
            });
        }

        public void Rollback()
        {
            ExecuteOnUiThread(() =>
            {
                CloseRecord();

                var currentDocumentPath = SafeGetActiveDocumentPath();
                if (!CanRollbackCurrentDocument(currentDocumentPath))
                {
                    Log.Warning(
                        "检测到活动文档已切换，取消自动回滚。InitialDocumentPath={InitialDocumentPath}, CurrentDocumentPath={CurrentDocumentPath}",
                        _initialDocumentPath,
                        currentDocumentPath);
                    return;
                }

                try
                {
                    _wordApplication?.Undo();
                    Log.Information("任务级 UndoRecord 已执行回滚。DocumentPath={DocumentPath}", currentDocumentPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(
                        ex,
                        "任务级 UndoRecord 回滚失败，仅能做最佳努力处理。DocumentPath={DocumentPath}",
                        currentDocumentPath);
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
            ExecuteOnUiThread(CloseRecord);
        }

        private void ExecuteOnUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            try
            {
                _wordApplicationWrapper.InvokeAsync(action).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "执行 UndoRecord UI 线程操作失败。");
            }
        }

        private void CloseRecord()
        {
            if (_recordClosed)
            {
                return;
            }

            _recordClosed = true;

            try
            {
                var undoRecord = _wordApplication == null ? null : _wordApplication.UndoRecord;
                if (undoRecord != null)
                {
                    undoRecord.EndCustomRecord();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "结束 Word UndoRecord 失败。");
            }
        }

        private string SafeGetActiveDocumentPath()
        {
            dynamic activeDocument = null;
            try
            {
                activeDocument = _wordApplication == null ? null : _wordApplication.ActiveDocument;
                return activeDocument == null ? string.Empty : Convert.ToString(activeDocument.FullName);
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                WordApplicationWrapper.TryReleaseComObjectSilently(activeDocument);
            }
        }

        private bool CanRollbackCurrentDocument(string currentDocumentPath)
        {
            if (string.IsNullOrWhiteSpace(_initialDocumentPath))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(currentDocumentPath))
            {
                return false;
            }

            return string.Equals(
                _initialDocumentPath,
                currentDocumentPath,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
