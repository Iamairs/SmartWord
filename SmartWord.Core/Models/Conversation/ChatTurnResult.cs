namespace SmartWord.Core.Models.Conversation
{
    public sealed class ChatTurnResult
    {
        public string SessionId { get; set; }

        public string AssistantReply { get; set; }

        public string PendingActionId { get; set; }

        public bool RequiresUserConfirmation { get; set; }

        public ConversationRouteType RouteType { get; set; }
    }
}
