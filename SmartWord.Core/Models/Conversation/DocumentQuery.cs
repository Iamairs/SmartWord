namespace SmartWord.Core.Models.Conversation
{
    public sealed class DocumentQuery
    {
        public string QueryText { get; set; }

        public string SelectedText { get; set; }

        public int MaxChunks { get; set; }

        public string ModelOverride { get; set; }
    }
}
