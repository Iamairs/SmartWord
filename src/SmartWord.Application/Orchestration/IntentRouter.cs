using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// 负责在 Ask / Plan / Agent 之间进行请求路由。
    /// </summary>
    public class IntentRouter
    {
        private static readonly TimeSpan RoutingStreamDisposeTimeout = TimeSpan.FromSeconds(2);

        private readonly ILlmClient _llmClient;
        private readonly string _lightModel;

        public IntentRouter(ILlmClient llmClient, string lightModel)
        {
            _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
            _lightModel = lightModel ?? string.Empty;
        }

        public async Task<AgentMode> RouteAsync(
            string userInput,
            DocumentContext documentContext,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userInput))
            {
                return AgentMode.Ask;
            }

            try
            {
                return await GetDecisionFromLlmAsync(userInput, documentContext, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                return RouteWithKeywords(userInput);
            }
        }

        private async Task<AgentMode> GetDecisionFromLlmAsync(
            string userInput,
            DocumentContext documentContext,
            CancellationToken cancellationToken)
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage
                {
                    Role = "system",
                    Content =
                        "你是一个意图分类器。根据用户输入和文档上下文，判断属于以下哪个模式：\n" +
                        "- ask：用户在询问、解释、分析文档内容，不需要修改文档\n" +
                        "- plan：用户需要复杂的多步骤操作，需要先规划再执行\n" +
                        "- agent：用户需要直接修改文档\n\n" +
                        "只回复一个单词：ask、plan 或 agent。"
                },
                new AgentMessage
                {
                    Role = "user",
                    Content = BuildClassificationInput(userInput, documentContext)
                }
            };

            var responseBuilder = new StringBuilder();
            using (var routingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var enumerator = _llmClient
                    .ChatCompletionStreamAsync(messages, _lightModel, routingCts.Token)
                    .GetAsyncEnumerator(routingCts.Token);

                try
                {
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        var chunk = enumerator.Current;
                        responseBuilder.Append(chunk);

                        var parsedDecision = ParseDecision(responseBuilder.ToString());
                        if (parsedDecision.HasValue)
                        {
                            Log.Information(
                                "意图路由已识别模式：Mode={Mode}, PartialResponse={PartialResponse}",
                                parsedDecision.Value,
                                responseBuilder.ToString());

                            routingCts.Cancel();
                            return parsedDecision.Value;
                        }
                    }
                }
                finally
                {
                    await DisposeEnumeratorSafelyAsync(
                            enumerator,
                            responseBuilder.ToString(),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return ParseDecision(responseBuilder.ToString()) ?? AgentMode.Agent;
        }

        private static async Task DisposeEnumeratorSafelyAsync(
            IAsyncEnumerator<string> enumerator,
            string partialResponse,
            CancellationToken callerCancellationToken)
        {
            try
            {
                var disposeTask = enumerator.DisposeAsync().AsTask();
                var timeoutTask = Task.Delay(RoutingStreamDisposeTimeout, CancellationToken.None);
                var completedTask = await Task.WhenAny(disposeTask, timeoutTask).ConfigureAwait(false);

                if (completedTask == timeoutTask)
                {
                    Log.Warning(
                        "意图路由流枚举器在 {TimeoutSeconds} 秒内未完成释放。PartialResponse={PartialResponse}",
                        RoutingStreamDisposeTimeout.TotalSeconds,
                        partialResponse);
                    return;
                }

                await disposeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "释放意图路由流枚举器时失败。PartialResponse={PartialResponse}",
                    partialResponse);
            }
        }

        private static string BuildClassificationInput(string userInput, DocumentContext documentContext)
        {
            var builder = new StringBuilder();
            builder.AppendLine("用户输入：");
            builder.AppendLine(userInput);
            builder.AppendLine();
            builder.AppendLine("文档上下文：");
            builder.AppendLine("DocumentName: " + (documentContext?.DocumentName ?? string.Empty));
            builder.AppendLine("DocumentPath: " + (documentContext?.DocumentPath ?? string.Empty));
            builder.AppendLine("HasSelection: " + (documentContext != null && documentContext.HasSelection));
            builder.AppendLine("SelectedText: " + (documentContext?.SelectedText ?? string.Empty));
            builder.AppendLine("DocumentStatus: " + (documentContext?.DocumentStatus?.GetUserFriendlyMessage() ?? string.Empty));
            return builder.ToString();
        }

        private static AgentMode? ParseDecision(string decision)
        {
            if (string.IsNullOrWhiteSpace(decision))
            {
                return null;
            }

            var normalized = decision.Trim().ToLowerInvariant();
            if (normalized.Contains("ask"))
            {
                return AgentMode.Ask;
            }

            if (normalized.Contains("plan"))
            {
                return AgentMode.Plan;
            }

            if (normalized.Contains("agent"))
            {
                return AgentMode.Agent;
            }

            return null;
        }

        private static AgentMode RouteWithKeywords(string userInput)
        {
            var normalized = (userInput ?? string.Empty).Trim();
            if (ContainsAny(normalized, "计划", "步骤", "方案", "先规划", "先列", "分几步"))
            {
                return AgentMode.Plan;
            }

            if (ContainsAny(normalized, "解释", "总结", "分析", "是什么", "为什么", "介绍", "概述"))
            {
                return AgentMode.Ask;
            }

            return AgentMode.Agent;
        }

        private static bool ContainsAny(string input, params string[] keywords)
        {
            foreach (var keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword)
                    && input.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
