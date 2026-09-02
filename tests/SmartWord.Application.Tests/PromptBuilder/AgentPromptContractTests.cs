using System;
using System.IO;
using Xunit;

namespace SmartWord.Application.Tests.PromptBuilder
{
    public class AgentPromptContractTests
    {
        [Fact]
        public void AgentPrompt_包含脚本同源筛选与精确验证约束()
        {
            var prompt = ReadPromptFile("AGENT.md");

            Assert.Contains("write_code` 与 `verify_code` 必须共享同一段目标筛选逻辑", prompt);
            Assert.Contains("写什么属性，就验证什么属性", prompt);
            Assert.Contains("写入方案 + 验证计划", prompt);
            Assert.Contains("系统会自动执行 `verify_code`", prompt);
            Assert.Contains("[SmartWord 自动验证结果]", prompt);
            Assert.Contains("只维护局部变量", prompt);
            Assert.Contains("return new { all_passed = allPassed, results = results };", prompt);
            Assert.Contains("不要先假设 `Information(...)`、`Style`、`Font`、`ParagraphFormat`", prompt);
        }

        [Fact]
        public void AgentPrompt_包含禁止空Catch与ReadScript探针规则()
        {
            var prompt = ReadPromptFile("AGENT.md");

            Assert.Contains("不要使用空的 `catch {}`", prompt);
            Assert.Contains("系统允许你使用 `read_script` 作为特权只读探针工具", prompt);
            Assert.Contains("首行缩进 2 字符", prompt);
            Assert.Contains("字体名 / 字号", prompt);
            Assert.Contains("Convert.ToBoolean(...)", prompt);
        }

        [Fact]
        public void PromptFiles_包含工具克制和简单任务协议()
        {
            var systemPrompt = ReadPromptFile("SYSTEM.md");
            var agentPrompt = ReadPromptFile("AGENT.md");
            var askPrompt = ReadPromptFile("ASK.md");
            var planPrompt = ReadPromptFile("PLAN.md");

            Assert.Contains("能直接回答时不要调用工具", systemPrompt);
            Assert.Contains("简单任务不需要 Todo Board", agentPrompt);
            Assert.Contains("同类安全改动应合并到一次 `patch_range.operations`", agentPrompt);
            Assert.Contains("不要把它当作所有问题的固定第一步", askPrompt);
            Assert.Contains("本轮必须调用至少一个与问题匹配的最窄只读工具刷新证据", askPrompt);
            Assert.Contains("不得仅凭历史回答或历史工具结果直接作答", askPrompt);
            Assert.Contains("本轮必须调用至少一个与任务匹配的最窄只读工具刷新证据", planPrompt);
            Assert.Contains("不得仅凭用户描述、历史回答或历史工具结果直接生成计划", planPrompt);
            Assert.DoesNotContain("每次新任务必须首先调用 probe_document", agentPrompt);
            Assert.Contains("不要把微小操作拆成过细 Todo", planPrompt);
            Assert.Contains("长文档执行“查找后处理”时", agentPrompt);
            Assert.Contains("只能选择一种范围", agentPrompt);
            Assert.Contains("工具返回字段或范围错误时", agentPrompt);
            Assert.Contains("每项必须包含 `type` 和非负 0-based", agentPrompt);
            Assert.Contains("批量操作最多 20 项", agentPrompt);
            Assert.Contains("`grep_document.scope` 必须是真实 JSON 对象", askPrompt);
            Assert.Contains("当前证据不完整", askPrompt);
        }

        private static string ReadPromptFile(string fileName)
        {
            var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (current != null)
            {
                var solutionPath = Path.Combine(current.FullName, "SmartWord.sln");
                if (File.Exists(solutionPath))
                {
                    var promptPath = Path.Combine(
                        current.FullName,
                        "src",
                        "SmartWord.AddIn",
                        "Resources",
                        "Prompts",
                        fileName);
                    return File.ReadAllText(promptPath);
                }

                current = current.Parent;
            }

            throw new FileNotFoundException("未找到仓库根目录下的提示词文件。", fileName);
        }
    }
}
