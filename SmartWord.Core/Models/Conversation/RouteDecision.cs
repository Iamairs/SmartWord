namespace SmartWord.Core.Models.Conversation
{
    public sealed class RouteDecision
    {
        public ConversationRouteType RouteType { get; set; }

        public double Confidence { get; set; }

        public string Reason { get; set; }
    }
}
