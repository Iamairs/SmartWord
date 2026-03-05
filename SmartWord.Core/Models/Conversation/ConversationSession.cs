using System;
using System.Collections.Generic;

namespace SmartWord.Core.Models.Conversation
{
    public sealed class ConversationSession
    {
        public ConversationSession()
        {
            Messages = new List<ConversationMessage>();
            PendingActions = new List<PendingAction>();
        }

        public string SessionId { get; set; }

        public string Title { get; set; }

        public bool IsActive { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        public List<ConversationMessage> Messages { get; set; }

        public List<PendingAction> PendingActions { get; set; }
    }
}
