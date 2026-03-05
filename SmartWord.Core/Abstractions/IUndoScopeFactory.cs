// 文件说明：
// 定义撤销作用域工厂，用于创建具名撤销单元。
namespace SmartWord.Core.Abstractions
{
    /// <summary>
    /// 撤销作用域工厂契约。
    /// </summary>
    public interface IUndoScopeFactory
    {
        /// <summary>
        /// 开启一个新的撤销作用域。
        /// </summary>
        /// <param name="name">撤销项名称，通常用于在 Word 撤销列表中展示。</param>
        /// <returns>撤销作用域实例。</returns>
        IUndoScope Begin(string name);
    }
}
