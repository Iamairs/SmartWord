using SmartWord.Core.Models.Conversation;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SmartWord.Core.Abstractions.Conversation
{
    public interface IConversationStore
    {
        Task<IReadOnlyList<ConversationSession>> LoadSessionsAsync();

        Task<ConversationSession> CreateSessionAsync(string title);

        Task<ConversationSession> GetSessionAsync(string sessionId);

        Task<ConversationSession> GetActiveSessionAsync();

        Task SetActiveSessionAsync(string sessionId);

        Task SaveSessionAsync(ConversationSession session);
    }
}
