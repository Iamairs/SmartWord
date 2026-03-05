using SmartWord.Core.Abstractions;

// 文件说明：
// Word 选区服务实现，封装选区读取与替换操作，屏蔽 COM 对象访问细节。
namespace SmartWord.Services.Selection
{
    /// <summary>
    /// Word 选区服务。
    /// </summary>
    public sealed class WordSelectionService : ISelectionService
    {
        private readonly dynamic _wordApplication;

        /// <summary>
        /// 初始化选区服务。
        /// </summary>
        /// <param name="wordApplication">Word 应用实例。</param>
        public WordSelectionService(dynamic wordApplication)
        {
            _wordApplication = wordApplication;
        }

        /// <summary>
        /// 获取当前选中文本。
        /// </summary>
        /// <returns>选区文本；读取失败时返回空字符串。</returns>
        public string GetSelectedText()
        {
            if (_wordApplication == null)
            {
                return string.Empty;
            }

            dynamic selection = _wordApplication.Selection;
            if (selection == null)
            {
                return string.Empty;
            }

            object text = selection.Text;
            return text as string ?? string.Empty;
        }

        /// <summary>
        /// 将当前选区替换为新文本。
        /// </summary>
        /// <param name="newText">新文本。</param>
        public void ReplaceSelection(string newText)
        {
            if (_wordApplication == null)
            {
                return;
            }

            dynamic selection = _wordApplication.Selection;
            if (selection == null)
            {
                return;
            }

            selection.Text = newText ?? string.Empty;
        }
    }
}
