using System.Collections.Generic;
using SmartWord.Core.Models;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 预留结构化压缩入口，当前阶段直接返回原始消息。
    /// </summary>
    public class ConversationCompressor
    {
        public IReadOnlyList<AgentMessage> Compress(IReadOnlyList<AgentMessage> messages)
        {
            return messages;
        }
    }
}
