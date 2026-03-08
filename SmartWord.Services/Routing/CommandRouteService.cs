using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Core.Models.Conversation;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// 文件说明：
// 指令路由服务实现，综合模型判定与规则兜底决定任务导向会话模式。
namespace SmartWord.Services.Routing
{
    /// <summary>
    /// 指令路由服务。
    /// </summary>
    public sealed class CommandRouteService : ICommandRouteService
    {
        private readonly IModelService _modelService;

        /// <summary>
        /// 初始化路由服务。
        /// </summary>
        /// <param name="modelService">模型服务。</param>
        public CommandRouteService(IModelService modelService)
        {
            _modelService = modelService;
        }

        /// <summary>
        /// 对输入指令进行路由判定。
        /// </summary>
        /// <param name="input">路由输入。</param>
        /// <returns>路由决策结果。</returns>
        public async Task<RouteDecision> DecideRouteAsync(RouteInput input)
        {
            if (input != null && input.ModeLock.HasValue)
            {
                ConversationRouteType locked = NormalizeRouteType(input.ModeLock.Value);
                return new RouteDecision
                {
                    RouteType = locked,
                    Confidence = 1.0d,
                    Reason = "用户已锁定模式。",
                    ModeReasonCategory = "mode-lock"
                };
            }

            if (input == null || string.IsNullOrWhiteSpace(input.UserMessage))
            {
                return new RouteDecision
                {
                    RouteType = ConversationRouteType.Writing,
                    Confidence = 0.5d,
                    Reason = "空指令默认走写作模式。",
                    ModeReasonCategory = "empty-input"
                };
            }

            try
            {
                // 优先使用模型路由，提升复杂场景识别准确性。
                string modelReply = await _modelService.ChatWithPromptsAsync(
                    BuildSystemPrompt(),
                    BuildUserPrompt(input),
                    input.ModelOverride,
                    0.0d).ConfigureAwait(false);

                RouteDecision parsed = ParseModelRoute(modelReply);
                if (parsed != null)
                {
                    return parsed;
                }
            }
            catch
            {
                // 路由失败时使用启发式兜底，避免阻断主流程。
            }

            return BuildFallbackRoute(input.UserMessage);
        }

        /// <summary>
        /// 构建路由系统提示词。
        /// </summary>
        private static string BuildSystemPrompt()
        {
            return "You are a routing agent for Word assistant. " +
                   "Return strict JSON only: {\"route\":\"qa|writing|processing|execute\",\"confidence\":0-1,\"reason\":\"...\",\"category\":\"...\"}. " +
                   "qa=question answering on document. writing=text rewriting or drafting. processing=structured extraction/organization. execute=formatting or script-like operation that needs confirmation.";
        }

        /// <summary>
        /// 构建路由用户提示词。
        /// </summary>
        private static string BuildUserPrompt(RouteInput input)
        {
            return "Instruction:\n" + (input.UserMessage ?? string.Empty) +
                   "\n\nSelectedText:\n" + (input.SelectedText ?? string.Empty) +
                   "\n\nRetrievedContext:\n" + (input.RetrievedContext ?? string.Empty);
        }

        /// <summary>
        /// 解析模型返回的 JSON 风格路由结果。
        /// </summary>
        private static RouteDecision ParseModelRoute(string modelReply)
        {
            if (string.IsNullOrWhiteSpace(modelReply))
            {
                return null;
            }

            string route = ExtractValue(modelReply, "route");
            string confidenceText = ExtractValue(modelReply, "confidence");
            string reason = ExtractValue(modelReply, "reason");
            string category = ExtractValue(modelReply, "category");

            ConversationRouteType routeType;
            if (!TryParseRoute(route, out routeType))
            {
                return null;
            }

            double confidence;
            if (!double.TryParse(confidenceText, out confidence))
            {
                confidence = 0.7d;
            }

            return new RouteDecision
            {
                RouteType = routeType,
                Confidence = Math.Max(0d, Math.Min(1d, confidence)),
                Reason = string.IsNullOrWhiteSpace(reason) ? "模型路由" : reason,
                ModeReasonCategory = string.IsNullOrWhiteSpace(category) ? "model-route" : category
            };
        }

        /// <summary>
        /// 将字符串路由值解析为枚举。
        /// </summary>
        private static bool TryParseRoute(string route, out ConversationRouteType routeType)
        {
            routeType = ConversationRouteType.Writing;
            if (string.IsNullOrWhiteSpace(route))
            {
                return false;
            }

            string value = route.Trim().ToLowerInvariant();
            if (value == "qa")
            {
                routeType = ConversationRouteType.Qa;
                return true;
            }

            if (value == "writing" || value == "rewrite")
            {
                routeType = ConversationRouteType.Writing;
                return true;
            }

            if (value == "processing")
            {
                routeType = ConversationRouteType.Processing;
                return true;
            }

            if (value == "execute" || value == "vba" || value == "hybrid")
            {
                routeType = ConversationRouteType.Execute;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 从 JSON 风格文本中提取指定字段值。
        /// </summary>
        private static string ExtractValue(string jsonLikeText, string key)
        {
            Match quoted = Regex.Match(
                jsonLikeText,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"",
                RegexOptions.IgnoreCase);

            if (quoted.Success)
            {
                return quoted.Groups[1].Value;
            }

            Match numeric = Regex.Match(
                jsonLikeText,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*([0-9.]+)",
                RegexOptions.IgnoreCase);

            if (numeric.Success)
            {
                return numeric.Groups[1].Value;
            }

            return string.Empty;
        }

        /// <summary>
        /// 构建基于规则的兜底路由结果。
        /// </summary>
        private static RouteDecision BuildFallbackRoute(string instruction)
        {
            string text = instruction ?? string.Empty;
            bool hasWritingIntent = Regex.IsMatch(text, "润色|改写|优化|rewrite|polish|重写|扩写|精简", RegexOptions.IgnoreCase);
            bool hasExecuteIntent = Regex.IsMatch(text, "排版|字体|字号|段落|加粗|斜体|格式|VBA|macro|format|目录|页码|首行缩进", RegexOptions.IgnoreCase);
            bool hasProcessingIntent = Regex.IsMatch(text, "提炼|抽取|整理|结构化|大纲|要点|清单|步骤|分类|归纳|总结", RegexOptions.IgnoreCase);
            bool hasQaIntent = Regex.IsMatch(text, "什么|为何|为什么|如何|是否|多少|哪|question|\\?", RegexOptions.IgnoreCase);

            // 按优先级识别：执行混合 > 执行 > 写作 > 处理 > 问答 > 默认写作。
            if (hasExecuteIntent && hasWritingIntent)
            {
                return new RouteDecision
                {
                    RouteType = ConversationRouteType.Execute,
                    Confidence = 0.68d,
                    Reason = "规则识别为执行与写作组合任务。",
                    ModeReasonCategory = "fallback-execute-hybrid"
                };
            }

            if (hasExecuteIntent)
            {
                return new RouteDecision
                {
                    RouteType = ConversationRouteType.Execute,
                    Confidence = 0.64d,
                    Reason = "规则识别为执行任务。",
                    ModeReasonCategory = "fallback-execute"
                };
            }

            if (hasWritingIntent)
            {
                return new RouteDecision
                {
                    RouteType = ConversationRouteType.Writing,
                    Confidence = 0.62d,
                    Reason = "规则识别为写作任务。",
                    ModeReasonCategory = "fallback-writing"
                };
            }

            if (hasProcessingIntent)
            {
                return new RouteDecision
                {
                    RouteType = ConversationRouteType.Processing,
                    Confidence = 0.61d,
                    Reason = "规则识别为结构化处理任务。",
                    ModeReasonCategory = "fallback-processing"
                };
            }

            if (hasQaIntent)
            {
                return new RouteDecision
                {
                    RouteType = ConversationRouteType.Qa,
                    Confidence = 0.60d,
                    Reason = "规则识别为文档问答任务。",
                    ModeReasonCategory = "fallback-qa"
                };
            }

            return new RouteDecision
            {
                RouteType = ConversationRouteType.Writing,
                Confidence = 0.58d,
                Reason = "默认写作兜底。",
                ModeReasonCategory = "fallback-default"
            };
        }

        /// <summary>
        /// 将历史兼容路由值归一化为新模式。
        /// </summary>
        /// <param name="routeType">原始路由类型。</param>
        /// <returns>归一化后的新模式。</returns>
        private static ConversationRouteType NormalizeRouteType(ConversationRouteType routeType)
        {
            if (routeType == ConversationRouteType.Rewrite)
            {
                return ConversationRouteType.Writing;
            }

            if (routeType == ConversationRouteType.Vba || routeType == ConversationRouteType.Hybrid)
            {
                return ConversationRouteType.Execute;
            }

            return routeType;
        }
    }
}
