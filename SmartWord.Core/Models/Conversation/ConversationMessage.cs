using System;

namespace SmartWord.Core.Models.Conversation
{
    public sealed class ConversationMessage
    {
        public string Role { get; set; }

        public string Content { get; set; }

        public DateTime TimestampUtc { get; set; }

        public string Metadata { get; set; }
    }
}
