using System;

namespace SmartWord.Core.Models.Conversation
{
    public sealed class PendingAction
    {
        public string ActionId { get; set; }

        public ConversationActionType ActionType { get; set; }

        public ConversationRouteType RouteType { get; set; }

        public string RewriteText { get; set; }

        public string VbaCode { get; set; }

        public string EntryPoint { get; set; }

        public bool IsApplied { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }
}
