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
        public async System.Threading.Tasks.Task ReadSectionTool_互斥范围同时传入_返回明确错误()
        {
            using var document = JsonDocument.Parse(
                "{\"heading\":\"第三章\",\"around_cursor\":true}");
            var tool = new ReadSectionTool(null);

            var result = await tool.ExecuteAsync(document.RootElement, null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("读取范围互斥", result.Output);
        }

        [Fact]
        public async System.Threading.Tasks.Task ReadSectionTool_未指定范围_拒绝默认回退()
        {
            using var document = JsonDocument.Parse("{}");
            var tool = new ReadSectionTool(null);

            var result = await tool.ExecuteAsync(document.RootElement, null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("必须明确指定一种读取范围", result.Output);
        }

        [Fact]
        public async System.Threading.Tasks.Task ReadTableTool_请求超大结果_返回边界错误()
        {
            using var document = JsonDocument.Parse("{\"table_index\":0,\"max_rows\":101}");
            var tool = new ReadTableTool(null);

            var result = await tool.ExecuteAsync(document.RootElement, null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("max_rows 必须是 1 到 100", result.Output);
        }

        [Fact]
        public async System.Threading.Tasks.Task ReadAnnotationsTool_请求零条结果_返回边界错误()
        {
            using var document = JsonDocument.Parse("{\"max_results\":0}");
            var tool = new ReadAnnotationsTool(null);

            var result = await tool.ExecuteAsync(document.RootElement, null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("max_results 必须是 1 到 100", result.Output);
        }

        [Fact]
        public async System.Threading.Tasks.Task GrepDocumentTool_Scope传字符串_返回类型错误()
        {
            using var document = JsonDocument.Parse(
                "{\"keyword\":\"风险\",\"scope\":\"{\\\"selection_only\\\":true}\"}");
            var tool = new GrepDocumentTool(null);

            var result = await tool.ExecuteAsync(document.RootElement, null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("scope 必须是 JSON 对象", result.Output);
        }

        [Fact]
        public async System.Threading.Tasks.Task GrepDocumentTool_Scope同时指定标题和选区_返回互斥错误()
        {
            using var document = JsonDocument.Parse(
                "{\"keyword\":\"风险\",\"scope\":{\"heading\":\"第三章\",\"selection_only\":true}}");
            var tool = new GrepDocumentTool(null);

            var result = await tool.ExecuteAsync(document.RootElement, null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("范围选择互斥", result.Output);
        }

        [Fact]
        public async System.Threading.Tasks.Task PatchRangeTool_样式操作缺少Style_返回字段错误()
        {
            using var document = JsonDocument.Parse(
                "{\"operations\":[{\"type\":\"set_paragraph_style\",\"paragraph_index\":2}]}" );
            var tool = new PatchRangeTool(null);

            var result = await tool.ExecuteAsync(document.RootElement, null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("必须提供非空 style", result.Output);
        }

        [Fact]
        public async System.Threading.Tasks.Task ReadScriptTool_CodeMissing_返回明确错误()
        {
            using var document = JsonDocument.Parse("{\"description\":\"查询标题\"}");
            var tool = new ReadScriptTool(null, null, null);

            var result = await tool.ExecuteAsync(document.RootElement, null, System.Threading.CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("查询脚本不能为空", result.Output);
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
