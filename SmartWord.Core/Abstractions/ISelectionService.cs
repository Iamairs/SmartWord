// 文件说明：
// 定义与 Word 选区交互的抽象，隔离宿主对象依赖。
namespace SmartWord.Core.Abstractions
{
    /// <summary>
    /// 文本选区服务契约。
    /// </summary>
    public interface ISelectionService
    {
        /// <summary>
        /// 获取当前选中文本。
        /// </summary>
        /// <returns>选区文本；无选区时由实现层决定返回空字符串或其他约定值。</returns>
        string GetSelectedText();

        /// <summary>
        /// 使用新文本替换当前选区内容。
        /// </summary>
        /// <param name="newText">替换后的文本。</param>
        void ReplaceSelection(string newText);
    }
}
