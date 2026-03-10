// 文件说明：
// 定义一次会话轮次的输入模型，包含用户消息与可选模型参数。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 对话轮次请求。
    /// </summary>
    public sealed class ChatTurnRequest
    {
        /// <summary>
        /// 会话 ID；为空时由编排器决定是否创建新会话。
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// 用户消息文本。
        /// </summary>
        public string UserMessage { get; set; }

        /// <summary>
        /// 模型覆盖项。
        /// </summary>
        public string ModelOverride { get; set; }

        /// <summary>
        /// Prompt 版本标识。
        /// </summary>
        public string PromptVersion { get; set; }

        /// <summary>
        /// 模式锁定项；为空表示自动识别模式。
        /// </summary>
        public ConversationRouteType? ModeLock { get; set; }

        /// <summary>
        /// BM25 召回候选数量；小于等于 0 时使用服务默认值。
        /// </summary>
        public int Bm25CandidateCount { get; set; }

        /// <summary>
        /// 向量召回候选数量；小于等于 0 时使用服务默认值。
        /// </summary>
        public int DenseCandidateCount { get; set; }

        /// <summary>
        /// 重排候选数量；小于等于 0 时使用服务默认值。
        /// </summary>
        public int RerankCandidateCount { get; set; }

        /// <summary>
        /// 合并上下文最大字符预算；小于等于 0 时使用服务默认值。
        /// </summary>
        public int MaxContextCharacters { get; set; }

        /// <summary>
        /// 最终片段邻近扩展窗口；小于 0 时使用服务默认值。
        /// </summary>
        public int NeighborWindow { get; set; }
    }
}
