// 文件说明：
// 定义一次会话轮次执行后的输出模型，包含回复内容、路由信息与确认态。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 对话轮次结果。
    /// </summary>
    public sealed class ChatTurnResult
    {
        /// <summary>
        /// 会话 ID。
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// 助手回复文本。
        /// </summary>
        public string AssistantReply { get; set; }

        /// <summary>
        /// 待执行动作 ID；为空表示本轮没有待确认动作。
        /// </summary>
        public string PendingActionId { get; set; }

        /// <summary>
        /// 是否需要用户确认后才能执行。
        /// </summary>
        public bool RequiresUserConfirmation { get; set; }

        /// <summary>
        /// 本轮路由类型。
        /// </summary>
        public ConversationRouteType RouteType { get; set; }
    }
}
