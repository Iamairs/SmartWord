namespace SmartWord.Core.Models.Conversation
{
    public sealed class RouteInput
    {
        public string UserMessage { get; set; }

        public string SelectedText { get; set; }

        public string RetrievedContext { get; set; }

        public string ModelOverride { get; set; }
    }
}
