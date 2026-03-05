namespace SmartWord.Core.Models.Conversation
{
    public sealed class ApplyActionResult
    {
        public string SessionId { get; set; }

        public string ActionId { get; set; }

        public bool Success { get; set; }

        public string Message { get; set; }
    }
}
