using System;

// 文件说明：
// 定义单条会话消息的数据结构，记录角色、内容与时间戳。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 会话消息。
    /// </summary>
    public sealed class ConversationMessage
    {
        /// <summary>
        /// 消息角色（例如 user/assistant/system）。
        /// </summary>
        public string Role { get; set; }

        /// <summary>
        /// 消息正文。
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// 消息创建时间（UTC）。
        /// </summary>
        public DateTime TimestampUtc { get; set; }

        /// <summary>
        /// 额外元数据（通常为 JSON 字符串或轻量标记）。
        /// </summary>
        public string Metadata { get; set; }
    }
}
