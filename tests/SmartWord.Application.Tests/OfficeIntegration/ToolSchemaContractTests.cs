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
        public void PatchRangeTool_InputSchema_暴露Operations数组()
        {
            var tool = new PatchRangeTool(null);

            Assert.True(tool.InputSchema.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("operations", out var operations));
            Assert.Equal(JsonValueKind.String, operations.GetProperty("type").ValueKind);
            Assert.Equal("array", operations.GetProperty("type").GetString());
            Assert.Contains("0-based", operations.GetProperty("description").GetString());
            Assert.Contains("不要传字符串化后的 JSON", operations.GetProperty("description").GetString());
        }

        [Fact]
        public void ExecuteScriptTool_InputSchema_暴露双脚本字段()
        {
            var tool = new ExecuteScriptTool(null, null, null);

            Assert.True(tool.InputSchema.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("write_code", out var writeCode));
            Assert.True(properties.TryGetProperty("verify_code", out var verifyCode));
            Assert.Contains("app/doc/WordApp/ActiveDoc", writeCode.GetProperty("description").GetString());
            Assert.Contains("all_passed", verifyCode.GetProperty("description").GetString());
            Assert.Contains("results", verifyCode.GetProperty("description").GetString());
        }

        [Fact]
        public void VerifyScriptTool_InputSchema_强调只读与结构化返回()
        {
            var tool = new VerifyScriptTool(null, null, null);

            Assert.True(tool.InputSchema.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("code", out var code));
            Assert.Contains("只读", code.GetProperty("description").GetString());
            Assert.Contains("all_passed", code.GetProperty("description").GetString());
            Assert.Contains("results", code.GetProperty("description").GetString());
        }
    }
}
