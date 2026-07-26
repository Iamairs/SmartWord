using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.OfficeIntegration.Tests.Infrastructure;
using Xunit;
using Word = Microsoft.Office.Interop.Word;

namespace SmartWord.OfficeIntegration.Tests
{
    [Collection(RealWordCollection.Name)]
    public sealed class OfficeToolIntegrationTests
    {
        [WordIntegrationFact]
        public async Task 只读工具_读取真实Word文档_返回稳定上下文()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var path = await session.CreateBasicFixtureAsync();
                await session.OpenDocumentAsync(path);
                await session.SetSelectionAsync(0, 4);
                var registry = ToolTestFactory.CreateRegistry(session.WordWrapper);

                var probe = await ExecuteAsync(registry.GetTool("probe_document"), "{\"include_stats\":true}");
                var section = await ExecuteAsync(registry.GetTool("read_section"), "{\"scope\":{\"type\":\"range\",\"from_para\":0,\"to_para\":2}}");
                var grep = await ExecuteAsync(registry.GetTool("grep_document"), "{\"keyword\":\"第一段内容\",\"max_results\":5}");
                var selection = await ExecuteAsync(registry.GetTool("get_selection_context"), "{\"window_before\":0,\"window_after\":1}");

                Assert.True(probe.Success, probe.Output);
                Assert.True(section.Success, section.Output);
                Assert.True(grep.Success, grep.Output);
                Assert.True(selection.Success, selection.Output);
                Assert.Contains("第一段内容", section.Output);
                Assert.Contains("第一段内容", grep.Output);
                Assert.Contains("基线标题", selection.Output);
            });
        }

        [WordIntegrationFact]
        public async Task PatchRange_文本和格式写入_真实文档可回读()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var path = await session.CreateBasicFixtureAsync();
                await session.OpenDocumentAsync(path);
                var registry = ToolTestFactory.CreateRegistry(session.WordWrapper);
                var headingStyle = await session.GetBuiltInStyleNameAsync(Word.WdBuiltinStyle.wdStyleHeading1);
                var input = "{\"operations\":["
                    + "{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"第一段已替换\"},"
                    + "{\"type\":\"insert_paragraph_after\",\"paragraph_index\":1,\"text\":\"新增段落\"},"
                    + "{\"type\":\"set_paragraph_style\",\"paragraph_index\":0,\"style\":\"" + EscapeJson(headingStyle) + "\"},"
                    + "{\"type\":\"delete_paragraph\",\"paragraph_index\":4}]}";

                var result = await ExecuteAsync(registry.GetTool("patch_range"), input);
                var text = await session.ReadActiveDocumentTextAsync();
                var style = await session.WordWrapper.GetParagraphStyleAsync(0);

                Assert.True(result.Success, result.Output);
                Assert.Contains("第一段已替换", text);
                Assert.Contains("新增段落", text);
                Assert.DoesNotContain("待删除段落", text);
                Assert.Equal(headingStyle, style);
            });
        }

        [WordIntegrationFact]
        public async Task 表格工具_读取并脚本写入真实表格_验证通过()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var path = await session.CreateTableFixtureAsync();
                await session.OpenDocumentAsync(path);
                var registry = ToolTestFactory.CreateRegistry(session.WordWrapper);
                var read = await ExecuteAsync(registry.GetTool("read_table"), "{\"table_index\":0,\"max_rows\":5,\"max_columns\":5}");
                var input = "{\"description\":\"修改表格单元格\",\"write_code\":\"dynamic table = ActiveDoc.Tables[1]; dynamic cell = table.Cell(2, 2); dynamic range = cell.Range; range.Text = \\\"B2已修改\\\"; return new { changed = true };\",\"verify_code\":\"dynamic table = ActiveDoc.Tables[1]; dynamic cell = table.Cell(2, 2); dynamic range = cell.Range; var ok = System.Convert.ToString(range.Text).Contains(\\\"B2已修改\\\"); return new { all_passed = ok, results = new [] { new { name = \\\"table_cell\\\", passed = ok } } };\"}";
                var write = await ExecuteAsync(registry.GetTool("execute_script"), input);
                var verify = await ExecuteAsync(registry.GetTool("verify_script"), "{\"description\":\"验证表格\",\"code\":\"dynamic table = ActiveDoc.Tables[1]; dynamic cell = table.Cell(2, 2); dynamic range = cell.Range; var ok = System.Convert.ToString(range.Text).Contains(\\\"B2已修改\\\"); return new { all_passed = ok, results = new [] { new { name = \\\"table_cell\\\", passed = ok } } };\"}");

                Assert.True(read.Success, read.Output);
                Assert.Contains("A1", read.Output);
                Assert.True(write.Success, write.Output);
                Assert.True(verify.Success, verify.Output);
                Assert.Contains("\"all_passed\":true", verify.Output.Replace(" ", string.Empty));
            });
        }

        [WordIntegrationFact]
        public async Task 页眉页脚脚本_写入并验证_结构化验证通过()
        {
            await StaWordTestHost.RunAsync(async session =>
            {
                var path = await session.CreateHeaderFooterFixtureAsync();
                await session.OpenDocumentAsync(path);
                var registry = ToolTestFactory.CreateRegistry(session.WordWrapper);
                var input = "{\"description\":\"修改页眉页脚\",\"write_code\":\"dynamic section = ActiveDoc.Sections[1]; section.Headers[1].Range.Text = \\\"SmartWord页眉\\\"; section.Footers[1].Range.Text = \\\"SmartWord页脚\\\"; return new { changed = true };\",\"verify_code\":\"dynamic section = ActiveDoc.Sections[1]; var headerOk = System.Convert.ToString(section.Headers[1].Range.Text).Contains(\\\"SmartWord页眉\\\"); var footerOk = System.Convert.ToString(section.Footers[1].Range.Text).Contains(\\\"SmartWord页脚\\\"); return new { all_passed = headerOk && footerOk, results = new [] { new { name = \\\"header\\\", passed = headerOk }, new { name = \\\"footer\\\", passed = footerOk } } };\"}";
                var write = await ExecuteAsync(registry.GetTool("execute_script"), input);
                var verify = await ExecuteAsync(registry.GetTool("verify_script"), "{\"description\":\"验证页眉页脚\",\"code\":\"dynamic section = ActiveDoc.Sections[1]; var headerOk = System.Convert.ToString(section.Headers[1].Range.Text).Contains(\\\"SmartWord页眉\\\"); var footerOk = System.Convert.ToString(section.Footers[1].Range.Text).Contains(\\\"SmartWord页脚\\\"); return new { all_passed = headerOk && footerOk, results = new [] { new { name = \\\"header\\\", passed = headerOk }, new { name = \\\"footer\\\", passed = footerOk } } };\"}");

                Assert.True(write.Success, write.Output);
                Assert.True(verify.Success, verify.Output);
                Assert.Contains("\"all_passed\":true", verify.Output.Replace(" ", string.Empty));
            });
        }

        internal static async Task<SmartWord.Core.Models.ToolCallResult> ExecuteAsync(
            SmartWord.Core.Interfaces.ITool tool,
            string inputJson)
        {
            using (var document = JsonDocument.Parse(inputJson))
            {
                return await tool.ExecuteAsync(document.RootElement, null, CancellationToken.None);
            }
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
