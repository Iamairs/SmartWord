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

        /// <summary>
        /// BM25 召回候选数量；小于等于 0 时使用默认值。
        /// </summary>
        public int Bm25CandidateCount { get; set; }

        /// <summary>
        /// 向量召回候选数量；小于等于 0 时使用默认值。
        /// </summary>
        public int DenseCandidateCount { get; set; }

        /// <summary>
        /// 重排阶段候选数量；小于等于 0 时使用默认值。
        /// </summary>
        public int RerankCandidateCount { get; set; }

        /// <summary>
        /// 上下文合并文本最大字符预算；小于等于 0 时使用默认值。
        /// </summary>
        public int MaxContextCharacters { get; set; }

        /// <summary>
        /// 最终片段拼装的邻近扩展窗口；小于等于 0 时不扩展。
        /// </summary>
        public int NeighborWindow { get; set; }

        /// <summary>
        /// 检索策略档位（例如 balanced/accuracy/latency），为空时由服务端默认策略决定。
        /// </summary>
        public string RetrievalProfile { get; set; }

        /// <summary>
        /// 结构化意图提示文本（由上层传入或服务端推断），用于增强标题/表格等定向检索。
        /// </summary>
        public string IntentHints { get; set; }

        /// <summary>
        /// 目标作用域（例如 paragraph/table/heading），为空表示不过滤。
        /// </summary>
        public string[] TargetScopes { get; set; }

        /// <summary>
        /// 是否要求结果可定位到明确锚点；为 true 时可用于过滤无法跳转的片段。
        /// </summary>
        public bool RequireAnchorNavigable { get; set; }
    }
}
