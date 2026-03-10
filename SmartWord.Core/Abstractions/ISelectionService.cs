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

        /// <summary>
        /// 选中指定段落范围并定位到该位置。
        /// </summary>
        /// <param name="startParagraphIndex">起始段落索引（1 基）。</param>
        /// <param name="endParagraphIndex">结束段落索引（1 基，且不小于起始索引）。</param>
        void SelectParagraphRange(int startParagraphIndex, int endParagraphIndex);
    }
}
