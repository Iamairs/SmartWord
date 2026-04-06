using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 抽象对话历史的持久化与读取能力。
    /// </summary>
    public interface IConversationStore
    {
        Task AppendUserMessageAsync(
            string documentPath,
            AgentMessage message,
            CancellationToken cancellationToken);

        Task AppendAssistantMessageAsync(
            string documentPath,
            AgentMessage message,
            CancellationToken cancellationToken);

        Task AppendToolResultAsync(
            string documentPath,
            string toolCallId,
            string toolName,
            string rawInput,
            ToolCallResult result,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<AgentMessage>> GetHistoryAsync(
            string documentPath,
            CancellationToken cancellationToken);

        int EstimateTokenCount(IReadOnlyCollection<AgentMessage> messages);
    }
}
