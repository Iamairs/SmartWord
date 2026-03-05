using SmartWord.Core.Models.Conversation;
using System.Collections.Generic;
using System.Threading.Tasks;

// 文件说明：
// 定义会话编排主入口，负责会话管理、轮次执行与待执行动作应用。
namespace SmartWord.Core.Orchestration.Conversation
{
    /// <summary>
    /// 对话编排器契约。
    /// </summary>
    public interface IConversationOrchestrator
    {
        /// <summary>
        /// 加载会话列表。
        /// </summary>
        /// <returns>会话只读列表。</returns>
        Task<IReadOnlyList<ConversationSession>> LoadSessionsAsync();

        /// <summary>
        /// 创建新会话。
        /// </summary>
        /// <param name="title">会话标题。</param>
        /// <returns>新建会话。</returns>
        Task<ConversationSession> CreateSessionAsync(string title);

        /// <summary>
        /// 设置当前活动会话。
        /// </summary>
        /// <param name="sessionId">目标会话 ID。</param>
        Task SetActiveSessionAsync(string sessionId);

        /// <summary>
        /// 执行一次完整对话轮次。
        /// </summary>
        /// <param name="request">轮次请求。</param>
        /// <returns>轮次执行结果。</returns>
        Task<ChatTurnResult> RunTurnAsync(ChatTurnRequest request);

        /// <summary>
        /// 应用已确认的待执行动作。
        /// </summary>
        /// <param name="sessionId">会话 ID。</param>
        /// <param name="actionId">动作 ID。</param>
        /// <returns>动作应用结果。</returns>
        Task<ApplyActionResult> ApplyPendingActionAsync(string sessionId, string actionId);
    }
}
