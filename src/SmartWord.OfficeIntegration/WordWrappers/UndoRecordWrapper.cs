using System;
using SmartWord.Core.Interfaces;

namespace SmartWord.OfficeIntegration.WordWrappers
{
    /// <summary>
    /// 提供任务级 UndoRecord 的最简包装。
    /// </summary>
    public sealed class UndoRecordWrapper : IUndoScope
    {
        private readonly dynamic _wordApplication;
        private bool _completed;

        public UndoRecordWrapper(object wordApplication)
        {
            _wordApplication = wordApplication;
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
            catch
            {
            }
        }

        public void Commit()
        {
            CompleteRecord();
        }

        public void Rollback()
        {
            CompleteRecord();
        }

        public void Dispose()
        {
            CompleteRecord();
        }

        private void CompleteRecord()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;

            try
            {
                var undoRecord = _wordApplication == null ? null : _wordApplication.UndoRecord;
                if (undoRecord != null)
                {
                    undoRecord.EndCustomRecord();
                }
            }
            catch
            {
            }
        }
    }
}
