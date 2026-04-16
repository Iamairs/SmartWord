using System.Reflection;
using System.Text.Json;
using SmartWord.OfficeIntegration.Tools;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class ToolInputValidationTests
    {
        [Fact]
        public async System.Threading.Tasks.Task PatchRangeTool_OperationsAsString_返回明确错误()
        {
            using var document = JsonDocument.Parse(
                "{\"description\":\"test\",\"operations\":\"[{\\\"type\\\":\\\"replace_text\\\",\\\"paragraph_index\\\":1,\\\"text\\\":\\\"abc\\\"}]\"}");
            var tool = new PatchRangeTool(null);

            var result = await tool.ExecuteAsync(document.RootElement, null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("operations 必须是 JSON 数组", result.Output);
            Assert.Contains("不要传字符串化后的 JSON", result.Output);
        }

        [Fact]
        public async System.Threading.Tasks.Task VerifyScriptTool_CodeMissing_返回明确错误()
        {
            using var document = JsonDocument.Parse("{\"description\":\"验证标题\"}");
            var tool = new VerifyScriptTool(null, null, null);

            var result = await tool.ExecuteAsync(document.RootElement, null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("验证脚本不能为空", result.Output);
        }

        [Fact]
        public void ExecuteScriptTool_RuntimeHelper_对ForeachCom枚举错误给出明确提示()
        {
            var method = typeof(ExecuteScriptTool).GetMethod(
                "BuildScriptRuntimeErrorMessage",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.NotNull(method);

            var message = Assert.IsType<string>(method.Invoke(
                null,
                new object[]
                {
                    new System.InvalidCastException(
                        "System.__ComObject ... IEnumerable ... DISPID_NEWENUM ... E_NOINTERFACE")
                }));

            Assert.Contains("不要对 Word COM 集合使用 foreach", message);
            Assert.Contains("1-based", message);
        }
    }
}
