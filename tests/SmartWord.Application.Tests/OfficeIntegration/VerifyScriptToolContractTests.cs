using System.Reflection;
using System.Text.Json;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class VerifyScriptToolContractTests
    {
        [Fact]
        public void ExecuteScriptTool_RuntimeErrorHelper_仍保持内部可复用()
        {
            var method = typeof(SmartWord.OfficeIntegration.Tools.ExecuteScriptTool).GetMethod(
                "BuildScriptRuntimeErrorMessage",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.NotNull(method);
        }

        [Fact]
        public void ToolJsonOptions_序列化中文时保持可读文本()
        {
            var optionsType = typeof(SmartWord.OfficeIntegration.Tools.VerifyScriptTool).Assembly
                .GetType("SmartWord.OfficeIntegration.Tools.ToolJsonOptions");
            Assert.NotNull(optionsType);

            var defaultField = optionsType.GetField(
                "Default",
                BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(defaultField);

            var options = Assert.IsType<JsonSerializerOptions>(defaultField.GetValue(null));
            var json = JsonSerializer.Serialize(
                new
                {
                    all_passed = false,
                    actual = "字体=+中文正文, 字号=14磅, 缩进=2字符"
                },
                options);

            Assert.Contains("字体=+中文正文", json);
            Assert.Contains("缩进=2字符", json);
            Assert.DoesNotContain("\\u5B57", json);
            Assert.DoesNotContain("\\u7F29", json);
            Assert.DoesNotContain("\\u002B", json);
        }
    }
}
