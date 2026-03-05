using System;
using System.Collections.Generic;

// 文件说明：
// 定义会话聚合根模型，聚合消息流与待执行动作集合。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 会话实体。
    /// </summary>
    public sealed class ConversationSession
    {
        /// <summary>
        /// 初始化会话实体。
        /// </summary>
        public ConversationSession()
        {
            // 提前初始化集合，避免调用方追加数据时触发空引用异常。
            Messages = new List<ConversationMessage>();
            PendingActions = new List<PendingAction>();
        }

        /// <summary>
        /// 会话唯一标识。
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// 会话标题。
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// 是否为当前活动会话。
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// 创建时间（UTC）。
        /// </summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>
        /// 最后更新时间（UTC）。
        /// </summary>
        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>
        /// 会话消息列表。
        /// </summary>
        public List<ConversationMessage> Messages { get; set; }

        /// <summary>
        /// 待执行动作列表。
        /// </summary>
        public List<PendingAction> PendingActions { get; set; }
    }
}
