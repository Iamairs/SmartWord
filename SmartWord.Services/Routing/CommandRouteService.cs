using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Core.Models.Conversation;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SmartWord.Services.Routing
{
    public sealed class CommandRouteService : ICommandRouteService
    {
        private readonly IModelService _modelService;

        public CommandRouteService(IModelService modelService)
        {
            _modelService = modelService;
        }

        public async Task<RouteDecision> DecideRouteAsync(RouteInput input)
        {
            if (input == null || string.IsNullOrWhiteSpace(input.UserMessage))
            {
                return new RouteDecision
                {
                    RouteType = ConversationRouteType.Rewrite,
                    Confidence = 0.5d,
                    Reason = "空指令默认走改写。"
                };
            }

            try
            {
                string modelReply = await _modelService.ChatWithPromptsAsync(
                    BuildSystemPrompt(),
                    BuildUserPrompt(input),
                    input.ModelOverride,
                    0.0d);

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

        private static string BuildSystemPrompt()
        {
            return "You are a routing agent for Word assistant. " +
                   "Return strict JSON only: {\"route\":\"rewrite|vba|hybrid\",\"confidence\":0-1,\"reason\":\"...\"}. " +
                   "Use route=vba for formatting/layout/macro intent, rewrite for rewriting text, hybrid for both.";
        }

        private static string BuildUserPrompt(RouteInput input)
        {
            return "Instruction:\n" + (input.UserMessage ?? string.Empty) +
                   "\n\nSelectedText:\n" + (input.SelectedText ?? string.Empty) +
                   "\n\nRetrievedContext:\n" + (input.RetrievedContext ?? string.Empty);
        }

        private static RouteDecision ParseModelRoute(string modelReply)
        {
            if (string.IsNullOrWhiteSpace(modelReply))
            {
                return null;
            }

            string route = ExtractValue(modelReply, "route");
            string confidenceText = ExtractValue(modelReply, "confidence");
            string reason = ExtractValue(modelReply, "reason");

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
                Reason = string.IsNullOrWhiteSpace(reason) ? "模型路由" : reason
            };
        }

        private static bool TryParseRoute(string route, out ConversationRouteType routeType)
        {
            routeType = ConversationRouteType.Rewrite;
            if (string.IsNullOrWhiteSpace(route))
            {
                return false;
            }

            string value = route.Trim().ToLowerInvariant();
            if (value == "vba")
            {
                routeType = ConversationRouteType.Vba;
                return true;
            }

            if (value == "hybrid")
            {
                routeType = ConversationRouteType.Hybrid;
                return true;
            }

            if (value == "rewrite")
            {
                routeType = ConversationRouteType.Rewrite;
                return true;
            }

            return false;
        }

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

        private static RouteDecision BuildFallbackRoute(string instruction)
        {
            string text = instruction ?? string.Empty;
            bool hasRewriteIntent = Regex.IsMatch(text, "润色|改写|优化|rewrite|polish|重写", RegexOptions.IgnoreCase);
            bool hasVbaIntent = Regex.IsMatch(text, "排版|字体|字号|段落|加粗|斜体|格式|VBA|macro|format", RegexOptions.IgnoreCase);

            if (hasRewriteIntent && hasVbaIntent)
            {
                return new RouteDecision
                {
                    RouteType = ConversationRouteType.Hybrid,
                    Confidence = 0.65d,
                    Reason = "规则识别为组合任务。"
                };
            }

            if (hasVbaIntent)
            {
                return new RouteDecision
                {
                    RouteType = ConversationRouteType.Vba,
                    Confidence = 0.62d,
                    Reason = "规则识别为排版任务。"
                };
            }

            return new RouteDecision
            {
                RouteType = ConversationRouteType.Rewrite,
                Confidence = 0.6d,
                Reason = "默认改写兜底。"
            };
        }
    }
}
