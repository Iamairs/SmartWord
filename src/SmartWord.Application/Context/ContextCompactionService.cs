using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 串联预算判断、轻裁剪、LLM 摘要、程序硬状态与规则兜底的上下文压缩服务。
    /// </summary>
    public sealed class ContextCompactionService
    {
        private const int RecentRawMessageCount = 8;

        private readonly ContextBudgetPolicy _budgetPolicy;
        private readonly LightToolResultPruner _lightToolResultPruner;
        private readonly OversizedToolResultTruncator _oversizedToolResultTruncator;
        private readonly ProgramHardStateBuilder _programHardStateBuilder;
        private readonly LlmHistoryCompactor _llmHistoryCompactor;
        private readonly ConversationCompressor _fallbackCompressor;

        public ContextCompactionService(
            ILlmClient llmClient,
            ConversationCompressor fallbackCompressor,
            ContextBudgetPolicy budgetPolicy = null,
            LightToolResultPruner lightToolResultPruner = null,
            OversizedToolResultTruncator oversizedToolResultTruncator = null,
            ProgramHardStateBuilder programHardStateBuilder = null,
            LlmHistoryCompactor llmHistoryCompactor = null)
        {
            if (llmClient == null)
            {
                throw new ArgumentNullException(nameof(llmClient));
            }

            _fallbackCompressor = fallbackCompressor ?? throw new ArgumentNullException(nameof(fallbackCompressor));
            _budgetPolicy = budgetPolicy ?? new ContextBudgetPolicy();
            _lightToolResultPruner = lightToolResultPruner ?? new LightToolResultPruner();
            _oversizedToolResultTruncator = oversizedToolResultTruncator ?? new OversizedToolResultTruncator();
            _programHardStateBuilder = programHardStateBuilder ?? new ProgramHardStateBuilder();
            _llmHistoryCompactor = llmHistoryCompactor ?? new LlmHistoryCompactor(llmClient);
        }

        public async Task<ContextCompactionResult> CompactIfNeededAsync(
            IReadOnlyList<AgentMessage> messages,
            AgentRunOptions options,
            ConversationCompressionContext context,
            CancellationToken cancellationToken)
        {
            if (messages == null || messages.Count == 0)
            {
                return ContextCompactionResult.NotNeeded(messages ?? Array.Empty<AgentMessage>());
            }

            var budget = _budgetPolicy.Resolve(options);
            var estimatedTokens = EstimateTokenCount(messages, budget);
            if (estimatedTokens < budget.SoftLimitTokens)
            {
                return ContextCompactionResult.NotNeeded(messages);
            }

            var stagedMessages = _oversizedToolResultTruncator.Truncate(messages, budget);
            stagedMessages = _lightToolResultPruner.Prune(stagedMessages);
            var stagedEstimate = EstimateTokenCount(stagedMessages, budget);
            if (stagedEstimate < budget.SoftLimitTokens)
            {
                return ContextCompactionResult.Compacted(
                    stagedMessages,
                    "当前对话已接近上下文上限，系统已轻量裁剪旧工具结果并继续执行。");
            }

            var programHardState = _programHardStateBuilder.Build(context);
            try
            {
                var summary = await _llmHistoryCompactor
                    .CompactAsync(stagedMessages, context, programHardState, options == null ? string.Empty : options.Model, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    var assembledMessages = AssembleCompactedMessages(stagedMessages, summary, programHardState);
                    var assembledEstimate = EstimateTokenCount(assembledMessages, budget);
                    if (assembledEstimate < budget.HardLimitTokens)
                    {
                        return ContextCompactionResult.Compacted(
                            assembledMessages,
                            "当前对话已接近上下文上限，系统已生成当前任务摘要并继续执行。");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // LLM 压缩失败时进入规则兜底，不能覆盖原历史状态。
            }

            var fallbackMessages = _fallbackCompressor.Compress(stagedMessages, context);
            fallbackMessages = EnsureFirstUserMessage(stagedMessages, fallbackMessages);
            var fallbackEstimate = EstimateTokenCount(fallbackMessages, budget);
            if (fallbackEstimate < budget.EmergencyLimitTokens && fallbackEstimate < estimatedTokens)
            {
                return ContextCompactionResult.Compacted(
                    fallbackMessages,
                    "当前对话已接近上下文上限，LLM 压缩不可用，系统已使用规则兜底压缩并继续执行。");
            }

            return ContextCompactionResult.Stop(
                fallbackMessages,
                "对话压缩后仍无法继续执行，本轮任务已停止。请缩小范围或拆分任务后再继续。");
        }

        public int EstimateTokenCount(IReadOnlyList<AgentMessage> messages, ContextBudgetSnapshot budget)
        {
            if (messages == null || messages.Count == 0)
            {
                return 0;
            }

            var totalChars = 0;
            foreach (var message in messages)
            {
                if (message == null)
                {
                    continue;
                }

                totalChars += (message.Content ?? string.Empty).Length;
                totalChars += (message.ReasoningContent ?? string.Empty).Length;
                totalChars += (message.RawToolInput ?? string.Empty).Length;
                if (message.ToolCalls != null)
                {
                    totalChars += message.ToolCalls.Sum(toolCall =>
                        toolCall == null
                            ? 0
                            : (toolCall.Input ?? string.Empty).Length
                                + (toolCall.Description ?? string.Empty).Length
                                + (toolCall.Name ?? string.Empty).Length);
                }
            }

            return _budgetPolicy.ApplySafetyMargin((int)Math.Ceiling(totalChars / 4.0), budget);
        }

        private static IReadOnlyList<AgentMessage> AssembleCompactedMessages(
            IReadOnlyList<AgentMessage> sourceMessages,
            string summary,
            string programHardState)
        {
            var source = sourceMessages
                .Where(message => message != null)
                .Select(ConversationMessageUtilities.CloneMessage)
                .ToList();
            var systemMessages = source
                .Where(message => ConversationMessageUtilities.IsRole(message, "system") && !message.IsCompressedSummary)
                .ToList();
            var nonSystemMessages = source
                .Where(message => !ConversationMessageUtilities.IsRole(message, "system"))
                .ToList();
            var firstUser = nonSystemMessages.FirstOrDefault(ConversationMessageUtilities.IsUserMessage);
            var recentStartIndex = ResolveRecentStartIndex(nonSystemMessages, RecentRawMessageCount);
            var recentMessages = BuildProtocolSafeRecentMessages(nonSystemMessages.Skip(recentStartIndex).ToList());
            var result = new List<AgentMessage>();
            result.AddRange(systemMessages);
            if (firstUser != null && !recentMessages.Any(message => IsSameMessage(message, firstUser)))
            {
                result.Add(ConversationMessageUtilities.CloneMessage(firstUser));
            }

            result.Add(new AgentMessage
            {
                Role = "system",
                Content = summary.Trim(),
                IsCompressedSummary = true
            });
            if (!string.IsNullOrWhiteSpace(programHardState))
            {
                result.Add(new AgentMessage
                {
                    Role = "system",
                    Content = programHardState.Trim(),
                    IsCompressedSummary = true
                });
            }

            result.AddRange(recentMessages);
            return result;
        }

        private static IReadOnlyList<AgentMessage> EnsureFirstUserMessage(
            IReadOnlyList<AgentMessage> sourceMessages,
            IReadOnlyList<AgentMessage> compactedMessages)
        {
            var firstUser = sourceMessages == null
                ? null
                : sourceMessages.FirstOrDefault(ConversationMessageUtilities.IsUserMessage);
            if (firstUser == null
                || compactedMessages == null
                || compactedMessages.Any(message => IsSameMessage(message, firstUser)))
            {
                return compactedMessages ?? Array.Empty<AgentMessage>();
            }

            var result = compactedMessages
                .Where(message => message != null)
                .Select(ConversationMessageUtilities.CloneMessage)
                .ToList();
            var insertIndex = result.FindLastIndex(message => ConversationMessageUtilities.IsRole(message, "system")) + 1;
            result.Insert(Math.Max(0, insertIndex), ConversationMessageUtilities.CloneMessage(firstUser));
            return result;
        }

        private static int ResolveRecentStartIndex(IReadOnlyList<AgentMessage> nonSystemMessages, int recentMessageCount)
        {
            var startIndex = Math.Max(0, nonSystemMessages.Count - Math.Max(1, recentMessageCount));
            if (startIndex >= nonSystemMessages.Count
                || !ConversationMessageUtilities.IsToolMessage(nonSystemMessages[startIndex]))
            {
                return startIndex;
            }

            var toolCallId = nonSystemMessages[startIndex].ToolCallId;
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                return startIndex;
            }

            for (var index = startIndex - 1; index >= 0; index--)
            {
                var candidate = nonSystemMessages[index];
                if (ConversationMessageUtilities.IsUserMessage(candidate))
                {
                    break;
                }

                if (ConversationMessageUtilities.IsAssistantMessage(candidate)
                    && candidate.ToolCalls != null
                    && candidate.ToolCalls.Any(toolCall =>
                        toolCall != null
                        && string.Equals(toolCall.Id, toolCallId, StringComparison.Ordinal)))
                {
                    return index;
                }
            }

            return startIndex;
        }

        private static List<AgentMessage> BuildProtocolSafeRecentMessages(IReadOnlyList<AgentMessage> recentMessages)
        {
            var result = new List<AgentMessage>();
            for (var index = 0; index < recentMessages.Count; index++)
            {
                var message = recentMessages[index];
                if (message == null)
                {
                    continue;
                }

                if (ConversationMessageUtilities.IsToolMessage(message))
                {
                    continue;
                }

                if (ConversationMessageUtilities.IsAssistantMessage(message)
                    && message.ToolCalls != null
                    && message.ToolCalls.Count > 0)
                {
                    var requiredToolCallIds = message.ToolCalls
                        .Where(toolCall => toolCall != null
                            && !string.IsNullOrWhiteSpace(toolCall.Id)
                            && !string.IsNullOrWhiteSpace(toolCall.Name))
                        .Select(toolCall => toolCall.Id)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    var toolResults = new List<AgentMessage>();
                    var observedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
                    var cursor = index + 1;
                    while (cursor < recentMessages.Count && ConversationMessageUtilities.IsToolMessage(recentMessages[cursor]))
                    {
                        var toolMessage = recentMessages[cursor];
                        if (!string.IsNullOrWhiteSpace(toolMessage.ToolCallId)
                            && requiredToolCallIds.Contains(toolMessage.ToolCallId)
                            && observedToolCallIds.Add(toolMessage.ToolCallId))
                        {
                            toolResults.Add(ConversationMessageUtilities.CloneMessage(toolMessage));
                        }

                        cursor++;
                    }

                    if (requiredToolCallIds.Count > 0 && observedToolCallIds.Count == requiredToolCallIds.Count)
                    {
                        result.Add(ConversationMessageUtilities.CloneMessage(message));
                        result.AddRange(toolResults);
                    }
                    else
                    {
                        var plainAssistantMessage = ConversationMessageUtilities.CloneMessage(message);
                        plainAssistantMessage.ToolCalls = new List<ToolCall>();
                        if (!string.IsNullOrWhiteSpace(plainAssistantMessage.Content))
                        {
                            result.Add(plainAssistantMessage);
                        }
                    }

                    index = cursor - 1;
                    continue;
                }

                result.Add(ConversationMessageUtilities.CloneMessage(message));
            }

            return result;
        }

        private static bool IsSameMessage(AgentMessage left, AgentMessage right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(left.LocalMessageId)
                && string.Equals(left.LocalMessageId, right.LocalMessageId, StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(left.Role, right.Role, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Content, right.Content, StringComparison.Ordinal)
                && string.Equals(left.ToolCallId, right.ToolCallId, StringComparison.Ordinal);
        }
    }

    public sealed class ContextCompactionResult
    {
        public IReadOnlyList<AgentMessage> Messages { get; set; } = Array.Empty<AgentMessage>();

        public bool WasCompacted { get; set; }

        public bool ShouldStop { get; set; }

        public string Message { get; set; } = string.Empty;

        public static ContextCompactionResult NotNeeded(IReadOnlyList<AgentMessage> messages)
        {
            return new ContextCompactionResult
            {
                Messages = messages ?? Array.Empty<AgentMessage>()
            };
        }

        public static ContextCompactionResult Compacted(
            IReadOnlyList<AgentMessage> messages,
            string message)
        {
            return new ContextCompactionResult
            {
                Messages = messages ?? Array.Empty<AgentMessage>(),
                WasCompacted = true,
                Message = message ?? string.Empty
            };
        }

        public static ContextCompactionResult Stop(
            IReadOnlyList<AgentMessage> messages,
            string message)
        {
            return new ContextCompactionResult
            {
                Messages = messages ?? Array.Empty<AgentMessage>(),
                WasCompacted = true,
                ShouldStop = true,
                Message = message ?? string.Empty
            };
        }
    }
}
