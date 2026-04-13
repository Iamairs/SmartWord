using System.Text.Json;
using SmartWord.OfficeIntegration.Tools;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class ToolSchemaContractTests
    {
        [Fact]
        public void ProbeDocumentTool_InputSchema_不再暴露IncludeStyles()
        {
            var tool = new ProbeDocumentTool(null);

            Assert.False(tool.InputSchema.TryGetProperty("include_styles", out _));
            Assert.True(tool.InputSchema.TryGetProperty("properties", out var properties));
            Assert.False(properties.TryGetProperty("include_styles", out _));
        }

        [Fact]
        public void ReadSectionTool_InputSchema_不再暴露IncludeFormatting()
        {
            var tool = new ReadSectionTool(null);

            Assert.True(tool.InputSchema.TryGetProperty("properties", out var properties));
            Assert.False(properties.TryGetProperty("include_formatting", out _));
        }

        [Fact]
        public void GrepDocumentTool_InputSchema_暴露Scope对象()
        {
            var tool = new GrepDocumentTool(null);

            Assert.True(tool.InputSchema.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("scope", out var scope));
            Assert.Equal(JsonValueKind.Object, scope.ValueKind);
        }
    }
}
