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
    }
}
