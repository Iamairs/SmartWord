// 文件说明：
// 定义文档检索请求模型，描述查询文本、选区上下文与返回数量。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 文档检索查询。
    /// </summary>
    public sealed class DocumentQuery
    {
        /// <summary>
        /// 检索查询文本。
        /// </summary>
        public string QueryText { get; set; }

        /// <summary>
        /// 当前选中文本，作为检索补充上下文。
        /// </summary>
        public string SelectedText { get; set; }

        /// <summary>
        /// 最大返回分片数。
        /// </summary>
        public int MaxChunks { get; set; }

        /// <summary>
        /// 模型覆盖项。
        /// </summary>
        public string ModelOverride { get; set; }
    }
}
