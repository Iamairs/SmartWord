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
