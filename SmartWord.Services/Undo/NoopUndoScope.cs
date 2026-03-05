using SmartWord.Core.Abstractions;

// 文件说明：
// 空操作撤销作用域，用于 Word UndoRecord 不可用时的降级占位。
namespace SmartWord.Services.Undo
{
    /// <summary>
    /// 空撤销作用域。
    /// </summary>
    internal sealed class NoopUndoScope : IUndoScope
    {
        /// <summary>
        /// 单例实例，避免重复分配无状态对象。
        /// </summary>
        public static readonly NoopUndoScope Instance = new NoopUndoScope();

        private NoopUndoScope()
        {
        }

        /// <summary>
        /// 空释放实现。
        /// </summary>
        public void Dispose()
        {
        }
    }
}
