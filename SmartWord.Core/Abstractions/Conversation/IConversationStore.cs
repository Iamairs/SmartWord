using SmartWord.Core.Models.Conversation;
using System.Collections.Generic;
using System.Threading.Tasks;

// 文件说明：
// 定义会话持久化能力抽象，负责会话列表、活动会话与单会话读写。
namespace SmartWord.Core.Abstractions.Conversation
{
    /// <summary>
    /// 会话存储契约。
    /// </summary>
    public interface IConversationStore
    {
        /// <summary>
        /// 加载全部会话。
        /// </summary>
        /// <returns>会话只读列表。</returns>
        Task<IReadOnlyList<ConversationSession>> LoadSessionsAsync();

        /// <summary>
        /// 创建新会话。
        /// </summary>
        /// <param name="title">会话标题。</param>
        /// <returns>新建后的会话对象。</returns>
        Task<ConversationSession> CreateSessionAsync(string title);

        /// <summary>
        /// 按会话 ID 获取会话。
        /// </summary>
        /// <param name="sessionId">会话 ID。</param>
        /// <returns>命中的会话；未命中时由实现层决定返回空值或异常。</returns>
        Task<ConversationSession> GetSessionAsync(string sessionId);

        /// <summary>
        /// 获取当前活动会话。
        /// </summary>
        /// <returns>活动会话。</returns>
        Task<ConversationSession> GetActiveSessionAsync();

        /// <summary>
        /// 设置活动会话。
        /// </summary>
        /// <param name="sessionId">目标会话 ID。</param>
        Task SetActiveSessionAsync(string sessionId);

        /// <summary>
        /// 保存会话。
        /// </summary>
        /// <param name="session">待保存会话对象。</param>
        Task SaveSessionAsync(ConversationSession session);
    }
}
