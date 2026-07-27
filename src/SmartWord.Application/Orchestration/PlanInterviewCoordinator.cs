using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// 负责 Plan 模式采访请求的解析、问答等待和轮次收口。
    /// </summary>
    internal sealed class PlanInterviewCoordinator
    {
        internal const int MaxInterviewRounds = 3;

        private readonly IQuestionChannel _questionChannel;

        internal PlanInterviewCoordinator(IQuestionChannel questionChannel)
        {
            _questionChannel = questionChannel;
        }

        internal bool IsInterviewCall(ToolCall toolCall, AgentMode mode)
        {
            return mode == AgentMode.Plan
                && toolCall != null
                && string.Equals(toolCall.Name, "ask_user_question", StringComparison.OrdinalIgnoreCase);
        }

        internal PlanInterviewRequest Prepare(ToolCall toolCall, int currentRound)
        {
            JObject input = null;
            try
            {
                input = string.IsNullOrWhiteSpace(toolCall?.Input)
                    ? new JObject()
                    : JObject.Parse(toolCall.Input);
            }
            catch
            {
                // 输入不合法时按缺少问题处理，由主循环回填标准工具错误。
            }

            var question = input?.Value<string>("question") ?? string.Empty;
            var options = (input?["options"] as JArray)?
                .Select(item => item.Value<string>() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray() ?? new string[0];
            var nextRound = currentRound + 1;

            return new PlanInterviewRequest
            {
                IsValid = !string.IsNullOrWhiteSpace(question),
                InterviewRound = nextRound,
                ReachedLimit = nextRound >= MaxInterviewRounds,
                Question = question,
                QuestionEvent = new AgentEvent
                {
                    Type = AgentEventType.QuestionAsked,
                    ToolCallId = toolCall?.Id ?? string.Empty,
                    ToolName = toolCall?.Name ?? string.Empty,
                    ToolInput = toolCall?.Input ?? string.Empty,
                    Content = question,
                    QuestionOptions = options,
                    RequiresConfirmation = true
                }
            };
        }

        internal async Task<string> WaitForAnswerAsync(string toolCallId, CancellationToken cancellationToken)
        {
            if (_questionChannel == null || !_questionChannel.IsAvailable)
            {
                return string.Empty;
            }

            return await _questionChannel
                .WaitForAnswerAsync(toolCallId ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }

        internal AgentMessage CreateRoundLimitMessage(string answer)
        {
            return new AgentMessage
            {
                Role = "user",
                Content = string.IsNullOrWhiteSpace(answer)
                    ? "[系统] 采访已达到最大轮次，请立即基于已收集信息输出执行计划，不得再提问。"
                    : $"用户回答：{answer}\n\n[系统] 采访已达到最大轮次，请立即输出执行计划，不得再提问。"
            };
        }
    }

    /// <summary>
    /// 表示一次已解析但尚未等待用户回答的采访请求。
    /// </summary>
    internal sealed class PlanInterviewRequest
    {
        internal bool IsValid { get; set; }

        internal int InterviewRound { get; set; }

        internal bool ReachedLimit { get; set; }

        internal string Question { get; set; } = string.Empty;

        internal AgentEvent QuestionEvent { get; set; }
    }
}
