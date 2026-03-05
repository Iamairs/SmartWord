// 文件说明：
// 定义检索命中的文档分片模型，包含文本内容与排序相关信息。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 检索分片。
    /// </summary>
    public sealed class RetrievedChunk
    {
        /// <summary>
        /// 分片唯一标识。
        /// </summary>
        public string ChunkId { get; set; }

        /// <summary>
        /// 分片文本。
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// 相关性分值。
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// 分片在文档中的位置索引。
        /// </summary>
        public int Position { get; set; }
    }
}
