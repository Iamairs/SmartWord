using System.Collections.Generic;

namespace SmartWord.Core.Models.Conversation
{
    public sealed class RetrievedContext
    {
        public RetrievedContext()
        {
            Chunks = new List<RetrievedChunk>();
        }

        public string DocumentId { get; set; }

        public List<RetrievedChunk> Chunks { get; set; }

        public string CombinedText { get; set; }
    }
}
