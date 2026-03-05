using SmartWord.Core.Models.Conversation;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SmartWord.Core.Orchestration.Conversation
{
    public interface IConversationOrchestrator
    {
        Task<IReadOnlyList<ConversationSession>> LoadSessionsAsync();

        Task<ConversationSession> CreateSessionAsync(string title);

        Task SetActiveSessionAsync(string sessionId);

        Task<ChatTurnResult> RunTurnAsync(ChatTurnRequest request);

        Task<ApplyActionResult> ApplyPendingActionAsync(string sessionId, string actionId);
    }
}
