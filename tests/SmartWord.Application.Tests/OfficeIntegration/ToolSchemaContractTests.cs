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

        [Fact]
        public void VerifyChangeTool_InputSchema_暴露Checks数组()
        {
            var tool = new VerifyChangeTool(null);

            Assert.True(tool.InputSchema.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("checks", out var checks));
            Assert.Equal(JsonValueKind.String, checks.GetProperty("type").ValueKind);
            Assert.Equal("array", checks.GetProperty("type").GetString());
        }

        [Fact]
        public void PatchRangeTool_InputSchema_暴露Operations数组()
        {
            var tool = new PatchRangeTool(null);

            Assert.True(tool.InputSchema.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("operations", out var operations));
            Assert.Equal(JsonValueKind.String, operations.GetProperty("type").ValueKind);
            Assert.Equal("array", operations.GetProperty("type").GetString());
        }

        [Fact]
        public void ExecuteScriptTool_InputSchema_描述可用脚本全局变量()
        {
            var tool = new ExecuteScriptTool(null, null, null);

            Assert.True(tool.InputSchema.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("code", out var code));
            Assert.Contains("app/doc/WordApp/ActiveDoc", code.GetProperty("description").GetString());
        }
    }
}
