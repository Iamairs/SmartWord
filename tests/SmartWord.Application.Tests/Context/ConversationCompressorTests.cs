using System.Collections.Generic;
using System.Linq;
using SmartWord.Application.Context;
using SmartWord.Core.Models;
using Xunit;

namespace SmartWord.Application.Tests.Context
{
    public class ConversationCompressorTests
    {
        [Fact]
        public void Compress_MessageCountNotExceeded_ReturnsOriginalShape()
        {
            var compressor = new ConversationCompressor();
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "user-1" },
                new AgentMessage { Role = "assistant", Content = "assistant-1" }
            };

            var result = compressor.Compress(messages);

            Assert.Equal(3, result.Count);
            Assert.DoesNotContain(result, item => item.IsCompressedSummary);
        }

        [Fact]
        public void Compress_MessageCountExceeded_PreservesSystemAndRecentMessages()
        {
            var compressor = new ConversationCompressor();
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "user-1" },
                new AgentMessage { Role = "assistant", Content = "assistant-1" },
                new AgentMessage { Role = "user", Content = "user-2" },
                new AgentMessage { Role = "assistant", Content = "assistant-2" },
                new AgentMessage { Role = "user", Content = "user-3" },
                new AgentMessage { Role = "assistant", Content = "assistant-3" },
                new AgentMessage { Role = "user", Content = "user-4" },
                new AgentMessage { Role = "assistant", Content = "assistant-4" }
            };

            var result = compressor.Compress(messages);

            Assert.Equal("system", result[0].Role);
            Assert.True(result[1].IsCompressedSummary);
            Assert.Contains("已压缩消息数：2", result[1].Content);
            Assert.Equal(
                new[] { "user-2", "assistant-2", "user-3", "assistant-3", "user-4", "assistant-4" },
                result.Skip(2).Select(item => item.Content).ToArray());
        }
    }
}
