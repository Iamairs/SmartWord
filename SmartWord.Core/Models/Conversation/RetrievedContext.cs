using System.Collections.Generic;

// 文件说明：
// 定义文档检索上下文模型，聚合分片结果与拼接文本。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 检索上下文。
    /// </summary>
    public sealed class RetrievedContext
    {
        /// <summary>
        /// 初始化检索上下文。
        /// </summary>
        public RetrievedContext()
        {
            // 提前初始化集合，避免调用方在追加分片时进行空值判断。
            Chunks = new List<RetrievedChunk>();
        }

        /// <summary>
        /// 文档唯一标识。
        /// </summary>
        public string DocumentId { get; set; }

        /// <summary>
        /// 检索命中的分片集合。
        /// </summary>
        public List<RetrievedChunk> Chunks { get; set; }

        /// <summary>
        /// 由分片拼接得到的合并文本。
        /// </summary>
        public string CombinedText { get; set; }
    }
}
