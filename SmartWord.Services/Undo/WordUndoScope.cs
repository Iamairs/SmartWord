using System;
using SmartWord.Core.Abstractions;

namespace SmartWord.Services.Undo
{
    internal sealed class WordUndoScope : IUndoScope
    {
        private readonly dynamic _undoRecord;
        private bool _disposed;

        public WordUndoScope(dynamic undoRecord)
        {
            _undoRecord = undoRecord;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _undoRecord.EndCustomRecord();
        }
    }
}
