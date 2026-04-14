using System.Text.Json;
using SmartWord.OfficeIntegration.Tools;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class PatchRangeRequestParserTests
    {
        [Fact]
        public void Parse_ValidOperations_ReturnsNormalizedRequest()
        {
            using var document = JsonDocument.Parse(
                "{\"description\":\"批量改写\",\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的第一段\"},{\"type\":\"set_paragraph_style\",\"paragraph_index\":1,\"style\":\"Heading 1\"}]}");

            var request = PatchRangeRequest.Parse(document.RootElement);

            Assert.Equal("批量改写", request.Description);
            Assert.Equal(2, request.Operations.Count);
            Assert.Equal("replace_text", request.Operations[0].Type);
            Assert.Equal("Heading 1", request.Operations[1].Style);
        }

        [Fact]
        public void Parse_InvalidEntries_AreIgnored()
        {
            using var document = JsonDocument.Parse(
                "{\"operations\":[{\"paragraph_index\":1},{\"type\":\"delete_paragraph\",\"paragraph_index\":2}]}");

            var request = PatchRangeRequest.Parse(document.RootElement);

            Assert.Single(request.Operations);
            Assert.Equal("delete_paragraph", request.Operations[0].Type);
            Assert.Equal(2, request.Operations[0].ParagraphIndex);
        }
    }
}
