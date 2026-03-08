using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Models;
using SmartWord.Core.Models.Conversation;

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
            string system = (systemPrompt ?? string.Empty).ToLowerInvariant();
            string prompt = (userPrompt ?? string.Empty).ToLowerInvariant();

            // 检索重排场景下，直接按输入顺序回传 chunk id，避免返回路由 JSON 干扰流程。
            if (system.Contains("retrieval reranker"))
            {
                MatchCollection matches = Regex.Matches(userPrompt ?? string.Empty, @"\b(p\d+)\s*:");
                if (matches.Count == 0)
                {
                    return Task.FromResult(string.Empty);
                }

                string[] ids = new string[matches.Count];
                for (int i = 0; i < matches.Count; i++)
                {
                    ids[i] = matches[i].Groups[1].Value;
                }

                return Task.FromResult(string.Join(",", ids));
            }

            bool isRouteScene = system.Contains("routing agent") || system.Contains("\"route\"");
            if (!isRouteScene)
            {
                // 非路由场景兜底为简单回答，避免误返回 JSON。
                return Task.FromResult("本地模型已收到请求，但当前为离线简化回答。");
            }

            bool hasWritingIntent = Regex.IsMatch(prompt, "润色|改写|优化|重写|rewrite|polish");
            bool hasExecuteIntent = Regex.IsMatch(prompt, "排版|格式|字体|字号|段落|vba|macro|format");
            bool hasProcessingIntent = Regex.IsMatch(prompt, "提炼|抽取|整理|结构化|大纲|要点|清单|步骤|分类|归纳");
            bool hasQaIntent = Regex.IsMatch(prompt, "什么|为何|为什么|如何|是否|多少|哪|question|\\?");

            string route = "writing";
            string reason = "local-fallback-default";
            if (hasExecuteIntent && hasWritingIntent)
            {
                route = "execute";
                reason = "local-fallback-hybrid";
            }
            else if (hasExecuteIntent)
            {
                route = "execute";
                reason = "local-fallback-execute";
            }
            else if (hasWritingIntent)
            {
                route = "writing";
                reason = "local-fallback-writing";
            }
            else if (hasProcessingIntent)
            {
                route = "processing";
                reason = "local-fallback-processing";
            }
            else if (hasQaIntent)
            {
                route = "qa";
                reason = "local-fallback-qa";
            }

            string json = "{\"route\":\"" + route + "\",\"confidence\":0.60,\"reason\":\"" + reason + "\",\"category\":\"" + reason + "\"}";
            return Task.FromResult(json);
        }

        /// <summary>
        /// 基于本地规则返回问答结果，用于离线降级。
        /// </summary>
        /// <param name="request">问答请求。</param>
        /// <returns>问答文本。</returns>
        public Task<string> AnswerQuestionAsync(DocumentQaRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Question))
            {
                return Task.FromResult(string.Empty);
            }

            string context = request.RetrievedContext ?? string.Empty;
            if (string.IsNullOrWhiteSpace(context))
            {
                return Task.FromResult("未检索到足够文档内容，建议缩小问题范围或先选中相关段落后重试。");
            }

            string selected = request.SelectedText ?? string.Empty;
            string answer = "基于当前文档内容，问题“" + request.Question.Trim() + "”的相关信息如下：\n"
                + context.Trim();

            if (!string.IsNullOrWhiteSpace(selected))
            {
                answer += "\n\n补充说明：你当前选中的内容已作为优先参考。";
            }

            return Task.FromResult(answer);
        }
    }
}
