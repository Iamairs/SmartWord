// 文件说明：
// 定义文档问答请求模型，聚合问题、选区与检索上下文。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 文档问答请求。
    /// </summary>
    public sealed class DocumentQaRequest
    {
        /// <summary>
        /// 用户问题。
        /// </summary>
        public string Question { get; set; }

        /// <summary>
        /// 当前选中文本。
        /// </summary>
        public string SelectedText { get; set; }

        /// <summary>
        /// 检索上下文。
        /// </summary>
        public string RetrievedContext { get; set; }

        /// <summary>
        /// 模型覆盖项。
        /// </summary>
        public string ModelOverride { get; set; }

        /// <summary>
        /// Prompt 版本。
        /// </summary>
        public string PromptVersion { get; set; }
    }
}
