using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Infrastructure.Persistence
{
    /// <summary>
    /// 预留 SQLite 对话存储骨架。
    /// </summary>
    public class SqliteConversationStore : IConversationStore
    {
        public Task AppendUserMessageAsync(
            string documentPath,
            AgentMessage message,
            CancellationToken cancellationToken)
        {
            _ = documentPath;
            _ = message;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotImplementedException("SQLite 对话持久化将在后续阶段接入。");
        }

        public Task AppendAssistantMessageAsync(
            string documentPath,
            AgentMessage message,
            CancellationToken cancellationToken)
        {
            _ = documentPath;
            _ = message;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotImplementedException("SQLite 对话持久化将在后续阶段接入。");
        }

        public Task AppendToolResultAsync(
            string documentPath,
            string toolCallId,
            string toolName,
            string rawInput,
            ToolCallResult result,
            CancellationToken cancellationToken)
        {
            _ = documentPath;
            _ = toolCallId;
            _ = toolName;
            _ = rawInput;
            _ = result;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotImplementedException("SQLite 对话持久化将在后续阶段接入。");
        }

        public Task<IReadOnlyList<AgentMessage>> GetHistoryAsync(
            string documentPath,
            CancellationToken cancellationToken)
        {
            _ = documentPath;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotImplementedException("SQLite 对话持久化将在后续阶段接入。");
        }

        public int EstimateTokenCount(IReadOnlyCollection<AgentMessage> messages)
        {
            if (messages == null)
            {
                return 0;
            }

            var total = 0;
            foreach (var message in messages)
            {
                if (message != null)
                {
                    total += (message.Content ?? string.Empty).Length;
                }
            }

            return total / 2;
        }
    }
}
