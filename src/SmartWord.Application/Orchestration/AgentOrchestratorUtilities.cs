using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SmartWord.Application.Context;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Todo;
using SmartWord.Application.Tools;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Core.Telemetry;
using SmartWord.OfficeIntegration.Tools;

namespace SmartWord.Application.Orchestration
{
    internal static class AgentOrchestratorUtilities
    {
        // 所有运行模式共用固定预算，避免用户配置突破编排器安全上限。
        private const int FixedIterationBudget = 100;
        private const int AskModeMaxIterations = FixedIterationBudget;
        private static readonly string[] DirectDocumentReferences =
        {
            "这篇文档",
            "当前文档",
            "该文档",
            "本篇文档",
            "本文",
            "这篇文章",
            "该文章",
            "此文",
            "文中",
            "当前文件",
            "本文件",
            "当前选区",
            "选中内容",
            "所选内容"
        };
        private static readonly string[] DocumentNouns =
        {
            "文档",
            "文章",
            "文件",
            "正文",
            "段落",
            "章节",
            "标题",
            "表格",
            "批注",
            "页眉",
            "页脚",
            "脚注",
            "尾注",
            "目录",
            "选区",
            "document",
            "paragraph",
            "section",
            "heading",
            "selection"
        };
        private static readonly string[] DocumentEvidenceIntents =
        {
            "总结",
            "概括",
            "内容",
            "主题",
            "要点",
            "大意",
            "结构",
            "提纲",
            "分析",
            "查找",
            "搜索",
            "引用",
            "是什么",
            "有哪些",
            "多少",
            "哪里",
            "规划",
            "计划",
            "修改",
            "改写",
            "润色",
            "翻译",
            "审阅",
            "优化",
            "调整",
            "重构",
            "summarize",
            "summary",
            "analyze",
            "review",
            "rewrite"
        };

        /// <summary>
        /// 判断 Ask/Plan 首轮是否必须刷新当前文档证据，避免模型凭历史或常识直接作答。
        /// </summary>
        internal static bool RequiresFreshDocumentToolCall(
            string userInput,
            AgentMode mode,
            int iteration)
        {
            if (iteration != 0
                || (mode != AgentMode.Ask && mode != AgentMode.Plan)
                || string.IsNullOrWhiteSpace(userInput))
            {
                return false;
            }

            var normalizedInput = userInput.Trim().ToLowerInvariant();
            if (DirectDocumentReferences.Any(reference =>
                normalizedInput.IndexOf(reference, StringComparison.Ordinal) >= 0))
            {
                return true;
            }

            if (Regex.IsMatch(
                normalizedInput,
                @"第\s*[0-9零一二三四五六七八九十百千万两]+\s*(段|页|章|节|行|个?标题|个?表格)"))
            {
                return true;
            }

            return DocumentNouns.Any(noun =>
                       normalizedInput.IndexOf(noun, StringComparison.Ordinal) >= 0)
                   && DocumentEvidenceIntents.Any(intent =>
                       normalizedInput.IndexOf(intent, StringComparison.Ordinal) >= 0);
        }

        internal static void NormalizeToolCalls(
            IReadOnlyList<ToolCall> toolCalls,
            AgentMode mode,
            int iteration)
        {
            if (toolCalls == null)
            {
                return;
            }

            for (var index = 0; index < toolCalls.Count; index++)
            {
                var toolCall = toolCalls[index];
                if (toolCall == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(toolCall.Id))
                {
                    toolCall.Id =
                        $"autogen_{mode.ToString().ToLowerInvariant()}_{iteration + 1}_{index + 1}_{System.Guid.NewGuid():N}";
                }

                toolCall.Name = toolCall.Name ?? string.Empty;
                toolCall.Input = toolCall.Input ?? string.Empty;
            }
        }

        internal static AgentMessage CloneMessage(AgentMessage message)
        {
            return new AgentMessage
            {
                Role = message.Role,
                Content = message.Content,
                ReasoningContent = message.ReasoningContent,
                ToolCallId = message.ToolCallId,
                Name = message.Name,
                IsCompressedSummary = message.IsCompressedSummary,
                IsInternalObservation = message.IsInternalObservation,
                InternalObservationKind = message.InternalObservationKind,
                ToolCalls = message.ToolCalls == null
                    ? new List<ToolCall>()
                    : message.ToolCalls.Select(item => new ToolCall
                    {
                        Id = item.Id,
                        Name = item.Name,
                        Input = item.Input,
                        Description = item.Description
                    }).ToList(),
                ToolName = message.ToolName,
                RawToolInput = message.RawToolInput,
                ToolSuccess = message.ToolSuccess
            };
        }

        internal static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxLength) + "...";
        }

        internal static int ResolveMaxIterations(AgentRunOptions options)
        {
            var configured = options != null && options.MaxIterations > 0
                ? options.MaxIterations
                : FixedIterationBudget;
            var bounded = Math.Min(FixedIterationBudget, configured);
            if (options != null && options.Mode == AgentMode.Ask)
            {
                return Math.Min(AskModeMaxIterations, bounded);
            }

            return bounded;
        }

        internal static AgentPermissionMode ResolvePermissionMode(AgentRunOptions options)
        {
            if (options != null && options.PermissionMode.HasValue)
            {
                return options.PermissionMode.Value;
            }

            return options != null && !options.RequireConfirmationForScripts
                ? AgentPermissionMode.AutoSafeWrites
                : AgentPermissionMode.ConfirmWrites;
        }

        internal static int ResolveTotalSteps(AgentRunOptions options)
        {
            return options == null || options.ActivePlan == null || options.ActivePlan.TodoList == null
                ? 0
                : options.ActivePlan.TodoList.Count;
        }

        internal static TaskRunCompletion ResolveTaskRunCompletion(
            bool completedSuccessfully,
            bool runPaused,
            TodoBoardRunOutcome interruptedOutcome,
            string interruptedReason,
            int completedSteps,
            int totalSteps)
        {
            if (completedSuccessfully)
            {
                return CreateTaskRunCompletion(
                    TaskRunStatus.Completed,
                    "任务已完成。",
                    completedSteps,
                    totalSteps);
            }

            if (runPaused)
            {
                return CreateTaskRunCompletion(
                    TaskRunStatus.Paused,
                    string.IsNullOrWhiteSpace(interruptedReason) ? "任务已暂停。" : interruptedReason,
                    completedSteps,
                    totalSteps);
            }

            if (interruptedOutcome == TodoBoardRunOutcome.Cancelled)
            {
                return CreateTaskRunCompletion(
                    TaskRunStatus.Cancelled,
                    string.IsNullOrWhiteSpace(interruptedReason) ? "用户取消任务。" : interruptedReason,
                    completedSteps,
                    totalSteps);
            }

            return CreateTaskRunCompletion(
                TaskRunStatus.Failed,
                string.IsNullOrWhiteSpace(interruptedReason) ? "任务未完成，系统已停止执行。" : interruptedReason,
                completedSteps,
                totalSteps);
        }

        internal static TaskRunCompletion CreateTaskRunCompletion(
            TaskRunStatus status,
            string message,
            int completedSteps,
            int totalSteps)
        {
            var safeMessage = Truncate(message ?? string.Empty, 300);
            return new TaskRunCompletion
            {
                Status = status,
                Summary = status == TaskRunStatus.Completed
                    ? "已完成任务。"
                    : safeMessage,
                FailureReason = status == TaskRunStatus.Failed ? safeMessage : string.Empty,
                CancelReason = status == TaskRunStatus.Cancelled ? safeMessage : string.Empty,
                CompletedSteps = completedSteps,
                TotalSteps = totalSteps,
                EndedAtUtc = DateTimeOffset.UtcNow
            };
        }

        internal static string ToTaskHistoryMode(AgentMode mode)
        {
            switch (mode)
            {
                case AgentMode.Plan:
                    return "plan";
                case AgentMode.Agent:
                    return "agent";
                case AgentMode.Ask:
                default:
                    return "ask";
            }
        }

        internal static string ToTaskHistoryPermissionMode(AgentPermissionMode mode)
        {
            switch (mode)
            {
                case AgentPermissionMode.ReadOnly:
                    return "read_only";
                case AgentPermissionMode.AutoSafeWrites:
                    return "auto_safe_writes";
                case AgentPermissionMode.FullAuto:
                    return "full_auto";
                case AgentPermissionMode.ConfirmWrites:
                default:
                    return "confirm_writes";
            }
        }

    }
}

