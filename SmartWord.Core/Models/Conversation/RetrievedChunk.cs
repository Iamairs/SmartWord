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

        /// <summary>
        /// 分片在文档中的结束位置索引。
        /// </summary>
        public int EndPosition { get; set; }

        /// <summary>
        /// 分片类型（如 Paragraph/Heading/TableCell）。
        /// </summary>
        public string ChunkType { get; set; }

        /// <summary>
        /// 章节路径（如 3.2.1 / 附录A），用于增强可追溯展示。
        /// </summary>
        public string HeadingPath { get; set; }

        /// <summary>
        /// Word 样式名称（如 Heading 1、Normal）。
        /// </summary>
        public string StyleName { get; set; }

        /// <summary>
        /// 引用角色（direct/supporting），用于区分主证据与补充证据。
        /// </summary>
        public string CitationType { get; set; }

        /// <summary>
        /// 权威度分值，综合结构信息得出。
        /// </summary>
        public double AuthorityScore { get; set; }
    }
}
