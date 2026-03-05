namespace SmartWord.Core.Models.Conversation
{
    public sealed class RetrievedChunk
    {
        public string ChunkId { get; set; }

        public string Text { get; set; }

        public double Score { get; set; }

        public int Position { get; set; }
    }
}
