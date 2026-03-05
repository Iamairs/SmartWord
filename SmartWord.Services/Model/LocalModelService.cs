using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Models;

// 文件说明：
// 本地模型降级实现，用于在远端模型不可用时维持基本改写、VBA 生成和路由能力。
namespace SmartWord.Services.Model
{
    /// <summary>
    /// 本地模型服务。
    /// </summary>
    public sealed class LocalModelService : IModelService
    {
        /// <summary>
        /// 基于规则执行文本改写。
        /// </summary>
        /// <param name="request">改写请求。</param>
        /// <returns>改写结果。</returns>
        public Task<string> RewriteTextAsync(EditorRewriteRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SelectedText))
            {
                return Task.FromResult(string.Empty);
            }

            string instruction = request.Instruction ?? string.Empty;
            string selectedText = request.SelectedText;

            // 简单关键词规则：仅用于离线兜底，不追求语义完备。
            if (instruction.IndexOf("upper", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Task.FromResult(selectedText.ToUpperInvariant());
            }

            if (instruction.IndexOf("lower", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Task.FromResult(selectedText.ToLowerInvariant());
            }

            if (instruction.IndexOf("formal", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Task.FromResult("Please note: " + selectedText);
            }

            return Task.FromResult(selectedText + " [Edited]");
        }

        /// <summary>
        /// 基于规则生成示例 VBA 代码。
        /// </summary>
        /// <param name="request">VBA 生成请求。</param>
        /// <returns>VBA 代码文本。</returns>
        public Task<string> GenerateVbaCodeAsync(VbaGenerationRequest request)
        {
            int fontSize = 16;
            if (request != null && !string.IsNullOrWhiteSpace(request.Instruction))
            {
                // 从指令中提取数字作为字号，限定到合理区间。
                Match match = Regex.Match(request.Instruction, "(\\d+)");
                int parsed;
                if (match.Success && int.TryParse(match.Value, out parsed) && parsed >= 6 && parsed <= 96)
                {
                    fontSize = parsed;
                }
            }

            string code =
                "Public Sub SmartWord_Run()" + "\r\n" +
                "    ActiveDocument.Content.Font.Size = " + fontSize + "\r\n" +
                "End Sub";

            return Task.FromResult(code);
        }

        /// <summary>
        /// 基于关键词规则返回路由 JSON，供路由服务解析。
        /// </summary>
        /// <param name="systemPrompt">系统提示词（本地实现中不使用）。</param>
        /// <param name="userPrompt">用户提示词。</param>
        /// <param name="modelOverride">模型覆盖项（本地实现中不使用）。</param>
        /// <param name="temperature">温度参数（本地实现中不使用）。</param>
        /// <returns>路由 JSON 文本。</returns>
        public Task<string> ChatWithPromptsAsync(string systemPrompt, string userPrompt, string modelOverride, double temperature)
        {
            string prompt = (userPrompt ?? string.Empty).ToLowerInvariant();
            bool hasRewriteIntent = prompt.Contains("润色") || prompt.Contains("改写") || prompt.Contains("rewrite");
            bool hasVbaIntent = prompt.Contains("排版") || prompt.Contains("格式") || prompt.Contains("vba") || prompt.Contains("macro");

            string route = "rewrite";
            if (hasRewriteIntent && hasVbaIntent)
            {
                route = "hybrid";
            }
            else if (hasVbaIntent)
            {
                route = "vba";
            }

            string json = "{\"route\":\"" + route + "\",\"confidence\":0.60,\"reason\":\"local-fallback\"}";
            return Task.FromResult(json);
        }
    }
}
