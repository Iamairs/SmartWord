using System;
using SmartWord.Core.Abstractions;

// 文件说明：
// Word 撤销作用域实现，负责结束由工厂启动的自定义撤销记录。
namespace SmartWord.Services.Undo
{
    /// <summary>
    /// Word 撤销作用域。
    /// </summary>
    internal sealed class WordUndoScope : IUndoScope
    {
        private readonly dynamic _undoRecord;
        private bool _disposed;

        /// <summary>
        /// 初始化撤销作用域。
        /// </summary>
        /// <param name="undoRecord">Word UndoRecord 对象。</param>
        public WordUndoScope(dynamic undoRecord)
        {
            _undoRecord = undoRecord;
        }

        /// <summary>
        /// 结束当前撤销记录。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // 与 StartCustomRecord 配对调用，保证撤销分组闭合。
            _undoRecord.EndCustomRecord();
        }
    }
}
