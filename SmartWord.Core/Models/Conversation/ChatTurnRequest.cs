namespace SmartWord.Core.Models.Conversation
{
    public sealed class ChatTurnRequest
    {
        public string SessionId { get; set; }

        public string UserMessage { get; set; }

        public string ModelOverride { get; set; }

        public string PromptVersion { get; set; }
    }
}
