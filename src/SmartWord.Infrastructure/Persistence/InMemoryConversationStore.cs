using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Infrastructure.Persistence
{
    /// <summary>
    /// Phase 1 使用内存会话存储，按编排器提供的文档/会话存储键隔离对话。
    /// </summary>
    public class InMemoryConversationStore : IConversationStore
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<AgentMessage>> _store =
            new ConcurrentDictionary<string, ConcurrentQueue<AgentMessage>>();

        public Task AppendUserMessageAsync(
            string documentPath,
            AgentMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetQueue(documentPath).Enqueue(CloneMessage(message));
            return Task.CompletedTask;
        }

        public Task AppendAssistantMessageAsync(
            string documentPath,
            AgentMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetQueue(documentPath).Enqueue(CloneMessage(message));
            return Task.CompletedTask;
        }

        public Task AppendToolResultAsync(
            string documentPath,
            string toolCallId,
            string toolName,
            string rawInput,
            ToolCallResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GetQueue(documentPath).Enqueue(new AgentMessage
            {
                Role = "tool",
                ToolCallId = toolCallId ?? string.Empty,
                Name = toolName ?? string.Empty,
                Content = result?.Output ?? string.Empty,
                ToolName = toolName ?? string.Empty,
                RawToolInput = rawInput ?? string.Empty,
                ToolSuccess = result != null && result.Success
            });

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentMessage>> GetHistoryAsync(
            string documentPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = GetQueue(documentPath)
                .Select(CloneMessage)
                .ToList()
                .AsReadOnly();
            return Task.FromResult((IReadOnlyList<AgentMessage>)history);
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
                if (message != null && !string.IsNullOrEmpty(message.Content))
                {
                    total += message.Content.Length;
                }
            }

            return total / 2;
        }

        private ConcurrentQueue<AgentMessage> GetQueue(string documentPath)
        {
            return _store.GetOrAdd(NormalizeDocumentPath(documentPath), _ => new ConcurrentQueue<AgentMessage>());
        }

        private static string NormalizeDocumentPath(string documentPath)
        {
            return string.IsNullOrWhiteSpace(documentPath) ? "__active_document__" : documentPath;
        }

        private static AgentMessage CloneMessage(AgentMessage message)
        {
            if (message == null)
            {
                return new AgentMessage();
            }

            return new AgentMessage
            {
                Role = message.Role,
                Content = message.Content,
                ReasoningContent = message.ReasoningContent,
                Name = message.Name,
                ToolCallId = message.ToolCallId,
                LocalMessageId = message.LocalMessageId,
                IsCompressedSummary = message.IsCompressedSummary,
                IsInternalObservation = message.IsInternalObservation,
                InternalObservationKind = message.InternalObservationKind,
                ToolName = message.ToolName,
                RawToolInput = message.RawToolInput,
                ToolSuccess = message.ToolSuccess,
                ToolCalls = message.ToolCalls == null
                    ? new List<ToolCall>()
                    : message.ToolCalls.Select(CloneToolCall).ToList()
            };
        }

        private static ToolCall CloneToolCall(ToolCall toolCall)
        {
            return new ToolCall
            {
                Id = toolCall.Id,
                Name = toolCall.Name,
                Input = toolCall.Input,
                Description = toolCall.Description
            };
        }
    }
}
