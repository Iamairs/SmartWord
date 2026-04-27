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
using SmartWord.OfficeIntegration.Tools;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// Ask/Plan/Agent 共用的主编排循环，Phase 2 先完整支持 Ask 模式只读工具链路。
    /// </summary>
    public sealed class AgentOrchestrator : IAgentOrchestrator
    {
        private const int FixedIterationBudget = 100;
        private const int AskModeMaxIterations = FixedIterationBudget;
        private const int MaxToolCallsPerIteration = 20;
        private const int ConsecutiveFailureThreshold = 3;
        private const int WriteRepairAttemptLimit = 3;
        private static readonly TimeSpan ToolExecutionTimeout = TimeSpan.FromSeconds(30);
        private const int ToolErrorMessageMaxLength = 500;
        private const int CompactionThreshold = 80000;

        private readonly ILlmClient _llmClient;
        private readonly IContextHydrator _contextHydrator;
        private readonly IConversationStore _conversationStore;
        private readonly SystemPromptBuilder _systemPromptBuilder;
        private readonly IToolRegistry _toolRegistry;
        private readonly PermissionGuard _permissionGuard;
        private readonly IConfirmationChannel _confirmationChannel;
        private readonly IQuestionChannel _questionChannel;
        private readonly IUndoScopeFactory _undoScopeFactory;
        private readonly ConversationCompressor _conversationCompressor;
        private readonly ITodoRecoveryChannel _todoRecoveryChannel;
        private readonly TodoManager _todoManager;
        private readonly TodoReminderService _todoReminderService;
        private readonly ITaskHistoryStore _taskHistoryStore;

        public AgentOrchestrator(
            ILlmClient llmClient,
            IContextHydrator contextHydrator,
            IConversationStore conversationStore,
            SystemPromptBuilder systemPromptBuilder,
            IToolRegistry toolRegistry,
            PermissionGuard permissionGuard,
            IConfirmationChannel confirmationChannel,
            IUndoScopeFactory undoScopeFactory,
            ConversationCompressor conversationCompressor,
            IQuestionChannel questionChannel = null,
            ITodoRecoveryChannel todoRecoveryChannel = null,
            TodoManager todoManager = null,
            TodoReminderService todoReminderService = null,
            ITaskHistoryStore taskHistoryStore = null)
        {
            _llmClient = llmClient;
            _contextHydrator = contextHydrator;
            _conversationStore = conversationStore;
            _systemPromptBuilder = systemPromptBuilder;
            _toolRegistry = toolRegistry;
            _permissionGuard = permissionGuard;
            _confirmationChannel = confirmationChannel;
            _questionChannel = questionChannel;
            _undoScopeFactory = undoScopeFactory;
            _conversationCompressor = conversationCompressor ?? throw new ArgumentNullException(nameof(conversationCompressor));
            _todoRecoveryChannel = todoRecoveryChannel;
            _todoManager = todoManager;
            _todoReminderService = todoReminderService;
            _taskHistoryStore = taskHistoryStore;
        }

        /// <summary>
        /// 运行一次 Agent 编排流程：
        /// 1) 读取文档上下文并拼装消息
        /// 2) 调用 LLM（可选工具调用）
        /// 3) 执行工具并回填结果
        /// 4) 输出流式事件与最终完成事件
        /// </summary>
        public async IAsyncEnumerable<AgentEvent> RunAsync(
    string userInput,
    AgentRunOptions options,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
{
    var safeOptions = options ?? new AgentRunOptions();
    var documentContext = await _contextHydrator.HydrateAsync(cancellationToken).ConfigureAwait(false);
    var documentPath = string.IsNullOrWhiteSpace(documentContext.DocumentPath)
        ? "__active_document__"
        : documentContext.DocumentPath;
    _todoManager?.SetCurrentDocumentPath(documentPath);
    var auditRun = await TryStartTaskRunAsync(
            documentPath,
            userInput,
            safeOptions,
            cancellationToken)
        .ConfigureAwait(false);
    TaskRunCompletion auditCompletion = null;
    var auditRunCompleted = false;

    if (safeOptions.Mode == AgentMode.Agent
        && (documentContext.DocumentStatus == null || !documentContext.DocumentStatus.IsWritable))
    {
        auditCompletion = CreateTaskRunCompletion(
            TaskRunStatus.Failed,
            documentContext.DocumentStatus == null
                ? "文档当前不可写，系统已停止执行。"
                : documentContext.DocumentStatus.GetUserFriendlyMessage(),
            0,
            ResolveTotalSteps(safeOptions));
        await TryCompleteTaskRunAsync(auditRun, auditCompletion, CancellationToken.None)
            .ConfigureAwait(false);
        auditRunCompleted = true;
        yield return new AgentEvent
        {
            Type = AgentEventType.DocumentNotWritable,
            Message = documentContext.DocumentStatus == null
                ? "文档当前不可写，系统已停止执行。"
                : documentContext.DocumentStatus.GetUserFriendlyMessage()
        };

        yield break;
    }

    var userMessage = new AgentMessage
    {
        Role = "user",
        Content = userInput ?? string.Empty
    };

    await _conversationStore
        .AppendUserMessageAsync(documentPath, userMessage, cancellationToken)
        .ConfigureAwait(false);

    TodoBoard currentTodoBoard = null;
    var activePlanFingerprint = _todoManager == null
        ? string.Empty
        : _todoManager.ComputePlanFingerprint(safeOptions.ActivePlan);
    var runStarted = false;
    if (_todoManager != null && safeOptions.Mode == AgentMode.Agent)
    {
        var prepareResult = await _todoManager
            .PrepareBoardForRunAsync(
                documentPath,
                safeOptions.ActivePlan,
                forceRebuildFromActivePlan: safeOptions.ActivePlan != null
                    && !safeOptions.StartupTodoBoardDecision.HasValue,
                cancellationToken)
            .ConfigureAwait(false);
        currentTodoBoard = prepareResult.Board;
        activePlanFingerprint = string.IsNullOrWhiteSpace(prepareResult.ActivePlanFingerprint)
            ? activePlanFingerprint
            : prepareResult.ActivePlanFingerprint;

        if (prepareResult.Status == TodoBoardPreparationStatus.RecoveryRequired
            || prepareResult.Status == TodoBoardPreparationStatus.Paused)
        {
            if (safeOptions.StartupTodoBoardDecision.HasValue)
            {
                currentTodoBoard = await _todoManager
                    .ResolveRecoveryAsync(
                        documentPath,
                        safeOptions.StartupTodoBoardDecision.Value,
                        safeOptions.ActivePlan,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (_todoRecoveryChannel == null || !_todoRecoveryChannel.IsAvailable)
            {
                yield return new AgentEvent
                {
                    Type = AgentEventType.Error,
                    Message = prepareResult.Status == TodoBoardPreparationStatus.Paused
                        ? "检测到已暂停的 Todo Board，但当前前端未连接继续决策通道，系统已停止执行。"
                        : "检测到待恢复的 Todo Board，但当前前端未连接恢复决策通道，系统已停止执行。"
                };

                yield break;
            }
            else
            {
                var recoveryRequestId = Guid.NewGuid().ToString("N");
                if (prepareResult.Status == TodoBoardPreparationStatus.Paused)
                {
                    yield return CreateTodoBoardPausedEvent(
                        prepareResult,
                        _todoManager,
                        recoveryRequestId);
                }
                else
                {
                    yield return CreateTodoBoardRecoveryRequiredEvent(
                        prepareResult,
                        _todoManager,
                        recoveryRequestId);
                }

                var recoveryDecision = await _todoRecoveryChannel
                    .WaitForDecisionAsync(recoveryRequestId, cancellationToken)
                    .ConfigureAwait(false);
                currentTodoBoard = await _todoManager
                    .ResolveRecoveryAsync(documentPath, recoveryDecision, safeOptions.ActivePlan, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var runId = Guid.NewGuid().ToString("N");
        currentTodoBoard = await _todoManager
            .MarkRunStartedAsync(documentPath, runId, activePlanFingerprint, cancellationToken)
            .ConfigureAwait(false);
        runStarted = true;
        yield return CreateTodoBoardReadyEvent(
            currentTodoBoard,
            _todoManager,
            prepareResult.Status == TodoBoardPreparationStatus.RecoveryRequired
                ? "Todo Board 已按恢复决策准备完毕。"
                : prepareResult.Status == TodoBoardPreparationStatus.Paused
                    ? "Todo Board 已按继续决策准备完毕。"
                : "当前 Todo Board 已就绪。");
    }

    var history = await _conversationStore
        .GetHistoryAsync(documentPath, cancellationToken)
        .ConfigureAwait(false);

    var messages = new List<AgentMessage>();
    var systemPrompt = BuildSystemPrompt(safeOptions, documentContext, currentTodoBoard);
    if (!string.IsNullOrWhiteSpace(systemPrompt))
    {
        messages.Add(new AgentMessage
        {
            Role = "system",
            Content = systemPrompt
        });
    }

    messages.AddRange(history);

    if (!string.IsNullOrWhiteSpace(safeOptions.ModelRoutingMessage))
    {
        Log.Information(
            "本次运行的模型能力分流说明：Mode={Mode}, Model={Model}, EnableToolCalling={EnableToolCalling}, RoutingMessage={RoutingMessage}",
            safeOptions.Mode,
            safeOptions.Model,
            safeOptions.EnableToolCalling,
            safeOptions.ModelRoutingMessage);
    }

    var toolDefinitions = safeOptions.EnableToolCalling
        ? _toolRegistry.GetToolDefinitions(safeOptions.Mode)
        : new List<ToolDefinition>();
    var citationRegistry = new Dictionary<int, CitationEntry>();
    var paragraphToRef = new Dictionary<int, int>();
    var nextCitationRef = 1;
    var maxIterations = ResolveMaxIterations(safeOptions);
    var consecutiveFailures = 0;
    var lastFailureSummary = string.Empty;
    var completedSuccessfully = false;
    var hasSuccessfulDocumentWriteOccurredInRun = false;
    var hasCompactedContext = false;
    var interviewRound = 0;
    const int MaxInterviewRounds = 3;
    PendingWriteStep pendingWriteStep = null;
    AgentMessage finalAssistantMessage = null;
    var interruptedOutcome = TodoBoardRunOutcome.None;
    var interruptedReason = string.Empty;
    var runPaused = false;
    var completedStepsForAudit = 0;
    var totalStepsForAudit = ResolveTotalSteps(safeOptions);

    try
    {
        var iteration = 0;
        for (; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var latestContext = await _contextHydrator.HydrateAsync(cancellationToken).ConfigureAwait(false);
            var latestDocumentPath = string.IsNullOrWhiteSpace(latestContext.DocumentPath)
                ? "__active_document__"
                : latestContext.DocumentPath;
            if (!string.Equals(latestDocumentPath, documentPath, StringComparison.OrdinalIgnoreCase))
            {
                yield return new AgentEvent
                {
                    Type = AgentEventType.DocumentMismatch,
                    Message = "检测到活动文档已切换，任务已停止。当前回滚仅能做最佳努力处理，请确认文档内容。"
                };

                interruptedOutcome = TodoBoardRunOutcome.Cancelled;
                interruptedReason = "检测到活动文档已切换，当前运行已停止。";
                yield break;
            }

            var compactionThreshold = safeOptions.CompactionThreshold > 0
                ? safeOptions.CompactionThreshold
                : CompactionThreshold;
            var estimatedTokenCount = _conversationStore.EstimateTokenCount(messages);
            if (estimatedTokenCount > compactionThreshold)
            {
                var compressionContext = CreateCompressionContext(
                    safeOptions,
                    documentPath,
                    latestContext,
                    currentTodoBoard,
                    pendingWriteStep,
                    messages);
                var compactedMessages = _conversationCompressor.Compress(messages, compressionContext);
                var compactedTokenCount = _conversationStore.EstimateTokenCount(compactedMessages);
                var canContinueWithCompactedContext = compactedMessages != null
                    && compactedMessages.Count < messages.Count
                    && compactedTokenCount < estimatedTokenCount;

                yield return new AgentEvent
                {
                    Type = AgentEventType.ContextCompacted,
                    Message = canContinueWithCompactedContext
                        ? "当前对话已接近上下文上限，系统已压缩较早消息并继续执行。"
                        : "当前对话已接近上下文上限，压缩后仍不足以继续执行，系统已停止本轮任务。"
                };

                if (!canContinueWithCompactedContext || hasCompactedContext)
                {
                    if (safeOptions.Mode == AgentMode.Agent)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "对话压缩后仍无法继续执行，系统已停止当前任务。";
                    }

                    yield return new AgentEvent
                    {
                        Type = AgentEventType.Error,
                        Message = "对话压缩后仍无法继续执行，本轮任务已停止。请缩小范围或拆分任务后再继续。"
                    };
                    yield break;
                }

                messages = compactedMessages.ToList();
                hasCompactedContext = true;
                continue;
            }

            AgentMessage assistantMessage;
            if (toolDefinitions.Count > 0)
            {
                var chunks = new ConcurrentQueue<string>();
                using (var signal = new SemaphoreSlim(0))
                {
                    var assistantTask = _llmClient.ChatCompletionWithToolsAsync(
                        messages,
                        safeOptions.Model,
                        toolDefinitions,
                        chunk =>
                        {
                            chunks.Enqueue(chunk);
                            signal.Release();
                        },
                        cancellationToken);

                    while (!assistantTask.IsCompleted || !chunks.IsEmpty)
                    {
                        while (chunks.TryDequeue(out var chunk))
                        {
                            yield return new AgentEvent
                            {
                                Type = AgentEventType.StreamChunk,
                                Content = chunk
                            };
                        }

                        if (assistantTask.IsCompleted)
                        {
                            break;
                        }

                        var waitTask = signal.WaitAsync(cancellationToken);
                        var completedTask = await Task.WhenAny(assistantTask, waitTask).ConfigureAwait(false);
                        if (completedTask == waitTask)
                        {
                            await waitTask.ConfigureAwait(false);
                        }
                    }

                    while (chunks.TryDequeue(out var remainingChunk))
                    {
                        yield return new AgentEvent
                        {
                            Type = AgentEventType.StreamChunk,
                            Content = remainingChunk
                        };
                    }

                    if (assistantTask.IsCanceled)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Cancelled;
                        interruptedReason = "当前 Agent 运行已被取消。";
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (assistantTask.IsFaulted)
                    {
                        var assistantException = assistantTask.Exception == null
                            ? null
                            : assistantTask.Exception.GetBaseException();
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = assistantException == null || string.IsNullOrWhiteSpace(assistantException.Message)
                            ? "当前 Agent 运行发生未预期异常。"
                            : assistantException.Message;
                        throw assistantException ?? new InvalidOperationException("当前 Agent 运行发生未预期异常。");
                    }

                    assistantMessage = assistantTask.Result;
                }
            }
            else
            {
                var builder = new StringBuilder();
                await foreach (var chunk in _llmClient.ChatCompletionStreamAsync(messages, safeOptions.Model, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                    {
                        continue;
                    }

                    builder.Append(chunk);
                    yield return new AgentEvent
                    {
                        Type = AgentEventType.StreamChunk,
                        Content = chunk
                    };
                }

                assistantMessage = new AgentMessage
                {
                    Role = "assistant",
                    Content = builder.ToString()
                };
            }

            NormalizeToolCalls(assistantMessage.ToolCalls, safeOptions.Mode, iteration);
            Log.Information(
                "编排器收到模型响应。Mode={Mode}, Iteration={Iteration}, ToolCallCount={ToolCallCount}, ToolSummary={ToolSummary}",
                safeOptions.Mode,
                iteration + 1,
                assistantMessage.ToolCalls == null ? 0 : assistantMessage.ToolCalls.Count,
                assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0
                    ? "none"
                    : string.Join(", ", assistantMessage.ToolCalls.Select(item => $"{item.Name}#{item.Id}")));

            finalAssistantMessage = assistantMessage;
            await _conversationStore
                .AppendAssistantMessageAsync(documentPath, assistantMessage, cancellationToken)
                .ConfigureAwait(false);
            messages.Add(CloneMessage(assistantMessage));

            if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
            {
                if (safeOptions.Mode == AgentMode.Plan)
                {
                    if (ExecutionPlanParser.TryParse(assistantMessage.Content, out var plan))
                    {
                        yield return new AgentEvent
                        {
                            Type = AgentEventType.PlanReady,
                            PlanJson = SerializeCamelCase(plan)
                        };
                        completedSuccessfully = true;
                    }
                    else
                    {
                        yield return new AgentEvent
                        {
                            Type = AgentEventType.Error,
                            Message = "当前规划输出未能解析为可执行计划，本轮任务已停止。请补充要求后继续规划。"
                        };
                        yield break;
                    }
                    break;
                }

                if (pendingWriteStep != null)
                {
                    var pauseMessage = "模型在当前写步骤仍待修复时提前停止输出，系统已回退当前步骤并暂停。你可以继续尝试、跳过此步骤，或停止本次任务。";
                    var pendingWriteStateEvent = CreatePendingWriteStateEvent(pendingWriteStep);
                    await TryRecordTaskChangeAsync(
                            auditRun?.Id,
                            pendingWriteStateEvent,
                            pendingWriteStateEvent.Type == AgentEventType.ChangeUnverified ? "unverified" : "repair_required",
                            cancellationToken)
                        .ConfigureAwait(false);
                    yield return pendingWriteStateEvent;
                    if (safeOptions.Mode == AgentMode.Agent && runStarted && _todoManager != null)
                    {
                        currentTodoBoard = await _todoManager
                            .MarkRunPausedAsync(
                                documentPath,
                                TodoBoardRunOutcome.RolledBack,
                                pauseMessage,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        runPaused = true;
                        auditCompletion = CreateTaskRunCompletion(
                            TaskRunStatus.Paused,
                            pauseMessage,
                            completedStepsForAudit,
                            totalStepsForAudit);
                        yield return CreateTodoBoardPausedEvent(
                            currentTodoBoard,
                            _todoManager,
                            pauseMessage);
                    }

                    yield break;
                }

                break;
            }

            var toolCalls = assistantMessage.ToolCalls;
            if (toolCalls.Count > MaxToolCallsPerIteration)
            {
                toolCalls = toolCalls.Take(MaxToolCallsPerIteration).ToList();
                Log.Warning(
                    "本轮工具调用数量超过限制，已截断。MaxToolCallsPerIteration={MaxToolCallsPerIteration}",
                    MaxToolCallsPerIteration);
            }

            var shouldContinueWithNextAssistantTurn = false;
            var remainingToolCallsStartIndex = -1;
            var remainingToolCallsReason = string.Empty;
            var todoWriteThisIteration = false;
            var hasEffectiveExecutionRoundThisIteration = false;
            var successfulDocumentWriteThisIteration = false;
            TodoReminderDecision reminderDecision = null;
            for (var toolCallIndex = 0; toolCallIndex < toolCalls.Count; toolCallIndex++)
            {
                var toolCall = toolCalls[toolCallIndex];
                cancellationToken.ThrowIfCancellationRequested();

                if (string.Equals(toolCall.Name, "ask_user_question", StringComparison.OrdinalIgnoreCase)
                    && safeOptions.Mode == AgentMode.Plan)
                {
                    JObject aqInput = null;
                    try { aqInput = string.IsNullOrWhiteSpace(toolCall.Input) ? new JObject() : JObject.Parse(toolCall.Input); }
                    catch { /* 解析失败则 question/options 为空 */ }

                    var question = aqInput?.Value<string>("question") ?? string.Empty;
                    var optionsToken = aqInput?["options"] as JArray;
                    var questionOptions = optionsToken != null
                        ? optionsToken
                            .Select(t => t.Value<string>() ?? string.Empty)
                            .Where(option => !string.IsNullOrWhiteSpace(option))
                            .ToArray()
                        : new string[0];

                    if (string.IsNullOrWhiteSpace(question))
                    {
                        var invalidQuestionResult = ToolCallResult.Error(
                            toolCall.Name,
                            "ask_user_question 缺少有效的 question 文本，系统已拒绝本次采访问题。");
                        await AppendToolResultAsync(documentPath, messages, toolCall, invalidQuestionResult, cancellationToken)
                            .ConfigureAwait(false);

                        yield return CreateToolCompletedEvent(toolCall, invalidQuestionResult);

                        RecordConsecutiveFailure(
                            ref consecutiveFailures,
                            ref lastFailureSummary,
                            toolCall.Name,
                            invalidQuestionResult.Output);
                        if (consecutiveFailures >= ConsecutiveFailureThreshold)
                        {
                            interruptedOutcome = TodoBoardRunOutcome.Failed;
                            interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                            yield return CreateCircuitBreakerEvent(lastFailureSummary);
                            yield break;
                        }

                        continue;
                    }

                    interviewRound++;
                    Log.Information(
                        "Plan 模式发起采访问题。Iteration={Iteration}, InterviewRound={InterviewRound}, ToolCallId={ToolCallId}, Question={Question}",
                        iteration + 1,
                        interviewRound,
                        toolCall.Id,
                        question);

                    yield return new AgentEvent
                    {
                        Type = AgentEventType.QuestionAsked,
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        ToolInput = toolCall.Input ?? string.Empty,
                        Content = question,
                        QuestionOptions = questionOptions,
                        RequiresConfirmation = true
                    };

                    string answer;
                    if (_questionChannel != null && _questionChannel.IsAvailable)
                    {
                        Log.Information(
                            "Plan 模式等待用户回答。ToolCallId={ToolCallId}",
                            toolCall.Id);
                        answer = await _questionChannel.WaitForAnswerAsync(toolCall.Id, cancellationToken).ConfigureAwait(false);
                        Log.Information(
                            "Plan 模式已收到用户回答。ToolCallId={ToolCallId}, AnswerLength={AnswerLength}",
                            toolCall.Id,
                            answer == null ? 0 : answer.Length);
                    }
                    else
                    {
                        // 无问答通道时降级：将问题作为文本输出，等待下一轮用户消息
                        answer = string.Empty;
                    }

                    if (interviewRound >= MaxInterviewRounds)
                    {
                        messages.Add(new AgentMessage
                        {
                            Role = "user",
                            Content = string.IsNullOrWhiteSpace(answer)
                                ? "[系统] 采访已达到最大轮次，请立即基于已收集信息输出执行计划，不得再提问。"
                                : $"用户回答：{answer}\n\n[系统] 采访已达到最大轮次，请立即输出执行计划，不得再提问。"
                        });
                    }
                    else
                    {
                        await AppendToolResultAsync(documentPath, messages, toolCall,
                            ToolCallResult.Ok($"用户回答：{answer}"), cancellationToken)
                            .ConfigureAwait(false);
                    }

                    shouldContinueWithNextAssistantTurn = true;
                    remainingToolCallsStartIndex = toolCallIndex + 1;
                    remainingToolCallsReason = "本轮已进入 Plan 采访等待状态，剩余工具调用已跳过。请在收到用户回答后再继续。";
                    break;
                }

                if (IsVerificationTool(toolCall.Name))
                {
                    var internalOnlyResult = ToolCallResult.Denied(
                        toolCall.Name,
                        "verify_script 为系统内部验证工具，不对模型暴露。请改用写工具或 read_script。");
                    await AppendToolResultAsync(documentPath, messages, toolCall, internalOnlyResult, cancellationToken)
                        .ConfigureAwait(false);

                    yield return new AgentEvent
                    {
                        Type = AgentEventType.ToolCallDenied,
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        ToolInput = toolCall.Input ?? string.Empty,
                        ToolOutput = internalOnlyResult.Output,
                        ToolSuccess = internalOnlyResult.Success,
                        OperationDescription = "内部验证工具不可直接调用。"
                    };

                    RecordConsecutiveFailure(
                        ref consecutiveFailures,
                        ref lastFailureSummary,
                        toolCall.Name,
                        internalOnlyResult.Output);
                    if (consecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                        yield return CreateCircuitBreakerEvent(lastFailureSummary);
                        yield break;
                    }

                    if (pendingWriteStep != null)
                    {
                        shouldContinueWithNextAssistantTurn = true;
                        remainingToolCallsStartIndex = toolCallIndex + 1;
                        remainingToolCallsReason = "当前轮次已停在待修复状态，剩余工具调用已跳过。";
                        break;
                    }

                    continue;
                }

                var tool = _toolRegistry.GetTool(toolCall.Name);
                // 只有真正修改 Word 文档的工具才进入写步骤修复与验证状态机。
                var isDocumentWriteTool = IsDocumentWriteTool(toolCall.Name);

                if (pendingWriteStep != null && !isDocumentWriteTool && !IsRepairProbeTool(toolCall.Name))
                {
                    var repairOnlyResult = ToolCallResult.Denied(
                        toolCall.Name,
                        "当前仍有待修复的写步骤。此时仅允许使用 read_script 做只读探针，或直接使用 patch_range / execute_script 修复当前失败步骤。");
                    await AppendToolResultAsync(documentPath, messages, toolCall, repairOnlyResult, cancellationToken)
                        .ConfigureAwait(false);

                    yield return new AgentEvent
                    {
                        Type = AgentEventType.ToolCallDenied,
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        ToolInput = toolCall.Input ?? string.Empty,
                        ToolOutput = repairOnlyResult.Output,
                        ToolSuccess = repairOnlyResult.Success,
                        OperationDescription = pendingWriteStep.OperationDescription
                    };

                    RecordConsecutiveFailure(
                        ref consecutiveFailures,
                        ref lastFailureSummary,
                        toolCall.Name,
                        repairOnlyResult.Output);
                    if (consecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                        yield return CreateCircuitBreakerEvent(lastFailureSummary);
                        yield break;
                    }

                    shouldContinueWithNextAssistantTurn = true;
                    remainingToolCallsStartIndex = toolCallIndex + 1;
                    remainingToolCallsReason = "当前仍有待修复的写步骤，剩余工具调用已跳过。";
                    break;
                }

                JObject parsedInput = null;
                ToolCallResult inputParseError = null;
                try
                {
                    parsedInput = string.IsNullOrWhiteSpace(toolCall.Input)
                        ? new JObject()
                        : JObject.Parse(toolCall.Input);
                }
                catch (Exception ex)
                {
                    inputParseError = ToolCallResult.Error(toolCall.Name, Truncate(ex.Message, ToolErrorMessageMaxLength));
                }

                var operationDescription = BuildOperationDescription(toolCall.Name, parsedInput);
                var permissionDecision = _permissionGuard.Decide(
                    toolCall.Name,
                    safeOptions.Mode,
                    ResolvePermissionMode(safeOptions));
                var requiresConfirmation = permissionDecision.RequiresConfirmation;

                yield return new AgentEvent
                {
                    Type = AgentEventType.ToolCallStarted,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.Name,
                    ToolInput = toolCall.Input ?? string.Empty,
                    RequiresConfirmation = requiresConfirmation,
                    OperationDescription = operationDescription
                };

                if (!permissionDecision.IsAllowed)
                {
                    var deniedResult = ToolCallResult.Denied(toolCall.Name, permissionDecision.Reason);
                    await AppendToolResultAsync(documentPath, messages, toolCall, deniedResult, cancellationToken, auditRun?.Id, operationDescription)
                        .ConfigureAwait(false);

                    yield return new AgentEvent
                    {
                        Type = AgentEventType.ToolCallDenied,
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        ToolInput = toolCall.Input ?? string.Empty,
                        ToolOutput = deniedResult.Output,
                        ToolSuccess = deniedResult.Success,
                        OperationDescription = operationDescription
                    };

                    RecordConsecutiveFailure(
                        ref consecutiveFailures,
                        ref lastFailureSummary,
                        toolCall.Name,
                        deniedResult.Output);
                    if (consecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                        yield return CreateCircuitBreakerEvent(lastFailureSummary);
                        yield break;
                    }

                    if (pendingWriteStep != null)
                    {
                        shouldContinueWithNextAssistantTurn = true;
                        remainingToolCallsStartIndex = toolCallIndex + 1;
                        remainingToolCallsReason = "当前轮次已停在待修复状态，剩余工具调用已跳过。";
                        break;
                    }

                    continue;
                }

                if (inputParseError != null)
                {
                    await AppendToolResultAsync(documentPath, messages, toolCall, inputParseError, cancellationToken, auditRun?.Id, operationDescription)
                        .ConfigureAwait(false);

                    yield return CreateToolCompletedEvent(toolCall, inputParseError);

                    RecordConsecutiveFailure(
                        ref consecutiveFailures,
                        ref lastFailureSummary,
                        toolCall.Name,
                        inputParseError.Output);
                    if (consecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                        yield return CreateCircuitBreakerEvent(lastFailureSummary);
                        yield break;
                    }

                    if (pendingWriteStep != null)
                    {
                        shouldContinueWithNextAssistantTurn = true;
                        remainingToolCallsStartIndex = toolCallIndex + 1;
                        remainingToolCallsReason = "当前轮次已停在待修复状态，剩余工具调用已跳过。";
                        break;
                    }

                    continue;
                }

                if (requiresConfirmation)
                {
                    if (_confirmationChannel == null || !_confirmationChannel.IsAvailable)
                    {
                        var unavailableResult = ToolCallResult.Denied(
                            toolCall.Name,
                            "当前未连接确认通道，系统已拒绝执行写操作。");
                        await AppendToolResultAsync(documentPath, messages, toolCall, unavailableResult, cancellationToken, auditRun?.Id, operationDescription)
                            .ConfigureAwait(false);

                        yield return new AgentEvent
                        {
                            Type = AgentEventType.ToolCallDenied,
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            ToolInput = toolCall.Input ?? string.Empty,
                            ToolOutput = unavailableResult.Output,
                            ToolSuccess = unavailableResult.Success,
                            OperationDescription = operationDescription
                        };

                        RecordConsecutiveFailure(
                            ref consecutiveFailures,
                            ref lastFailureSummary,
                            toolCall.Name,
                            unavailableResult.Output);
                        if (consecutiveFailures >= ConsecutiveFailureThreshold)
                        {
                            interruptedOutcome = TodoBoardRunOutcome.Failed;
                            interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                            yield return CreateCircuitBreakerEvent(lastFailureSummary);
                            yield break;
                        }

                        if (pendingWriteStep != null)
                        {
                            shouldContinueWithNextAssistantTurn = true;
                            remainingToolCallsStartIndex = toolCallIndex + 1;
                            remainingToolCallsReason = "当前轮次已停在待修复状态，剩余工具调用已跳过。";
                            break;
                        }

                        continue;
                    }

                    var confirmed = await _confirmationChannel
                        .WaitForConfirmationAsync(toolCall.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (!confirmed)
                    {
                        var skippedResult = ToolCallResult.Skipped(toolCall.Name, "用户选择跳过本次写操作。");
                        await AppendToolResultAsync(documentPath, messages, toolCall, skippedResult, cancellationToken, auditRun?.Id, operationDescription)
                            .ConfigureAwait(false);

                        yield return new AgentEvent
                        {
                            Type = AgentEventType.ToolCallSkipped,
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            ToolInput = toolCall.Input ?? string.Empty,
                            ToolOutput = skippedResult.Output,
                            ToolSuccess = skippedResult.Success,
                            OperationDescription = operationDescription
                        };

                        consecutiveFailures = 0;
                        if (pendingWriteStep != null)
                        {
                            shouldContinueWithNextAssistantTurn = true;
                            remainingToolCallsStartIndex = toolCallIndex + 1;
                            remainingToolCallsReason = "当前轮次已停在待修复状态，剩余工具调用已跳过。";
                            break;
                        }

                        continue;
                    }
                }

                ToolCallResult executionResult;
                IUndoScope writeStepUndoScope = null;
                var writeStepCommitted = false;
                var writeStepRolledBack = false;
                var todoWriteStepStarted = false;
                try
                {
                    if (tool == null)
                    {
                        executionResult = ToolCallResult.Error(toolCall.Name, "未找到对应的工具实现。");
                    }
                    else
                    {
                        if (isDocumentWriteTool
                            && safeOptions.Mode == AgentMode.Agent
                            && _undoScopeFactory != null)
                        {
                            writeStepUndoScope = await _undoScopeFactory
                                .BeginWriteStepUndoAsync("SmartWord Agent 写步骤", cancellationToken)
                                .ConfigureAwait(false);
                            if (_todoManager != null)
                            {
                                currentTodoBoard = await _todoManager
                                    .MarkWriteStepStartedAsync(
                                        documentPath,
                                        toolCall.Id,
                                        operationDescription,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                todoWriteStepStarted = true;
                            }
                        }

                        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            timeoutCts.CancelAfter(ToolExecutionTimeout);
                            using (var inputDocument = JsonDocument.Parse(parsedInput.ToString(Formatting.None)))
                            {
                                var toolTask = tool.ExecuteAsync(
                                    inputDocument.RootElement.Clone(),
                                    writeStepUndoScope,
                                    timeoutCts.Token);
                                var completedTask = await Task.WhenAny(
                                        toolTask,
                                        Task.Delay(ToolExecutionTimeout, cancellationToken))
                                    .ConfigureAwait(false);
                                executionResult = completedTask == toolTask
                                    ? await toolTask.ConfigureAwait(false)
                                    : ToolCallResult.Error(toolCall.Name, "工具执行超时。");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    interruptedOutcome = TodoBoardRunOutcome.Cancelled;
                    interruptedReason = "用户已取消当前任务。";
                    if (writeStepUndoScope != null && !writeStepCommitted && !writeStepRolledBack)
                    {
                        writeStepUndoScope.Rollback();
                        writeStepRolledBack = true;
                    }

                    if (todoWriteStepStarted && _todoManager != null)
                    {
                        currentTodoBoard = await _todoManager
                            .RollbackCurrentWriteStepAsync(
                                documentPath,
                                interruptedReason,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    throw;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    executionResult = ToolCallResult.Error(toolCall.Name, "工具执行超时。");
                }
                catch (Exception ex)
                {
                    executionResult = ToolCallResult.Error(
                        toolCall.Name,
                        Truncate(ex.ToString(), ToolErrorMessageMaxLength));
                }

                if (executionResult.Success)
                {
                    executionResult.Output = DecorateToolOutput(
                        toolCall.Name,
                        executionResult.Output,
                        documentPath,
                        citationRegistry,
                        paragraphToRef,
                        ref nextCitationRef);
                }

                if (string.IsNullOrWhiteSpace(executionResult.OperationDescription))
                {
                    executionResult.OperationDescription = operationDescription;
                }

                await AppendToolResultAsync(documentPath, messages, toolCall, executionResult, cancellationToken, auditRun?.Id, operationDescription)
                    .ConfigureAwait(false);

                yield return CreateToolCompletedEvent(toolCall, executionResult);
                if (tool != null && !IsTodoToolName(toolCall.Name))
                {
                    hasEffectiveExecutionRoundThisIteration = true;
                }

                if (TryGetTodoToolMetadata(executionResult, out var todoToolMetadata))
                {
                    todoWriteThisIteration = todoWriteThisIteration || todoToolMetadata.IsWriteOperation;
                    currentTodoBoard = DeserializeTodoBoard(todoToolMetadata.BoardJson);
                    yield return CreateTodoBoardEvent(
                        AgentEventType.TodoBoardUpdated,
                        todoToolMetadata,
                        todoToolMetadata.IsWriteOperation
                            ? TodoBoardUpdateKind.ToolWriteSync
                            : TodoBoardUpdateKind.ToolReadSync,
                        todoToolMetadata.IsWriteOperation ? "Todo Board 已更新。" : "Todo Board 已同步。");
                }

                if (executionResult.Success)
                {
                    if (isDocumentWriteTool)
                    {
                        consecutiveFailures = 0;
                        var autoVerifyPlan = BuildAutoVerifyPlan(toolCall.Name, parsedInput);
                        var executedWriteStep = pendingWriteStep != null
                            ? pendingWriteStep.MarkWriteExecuted(toolCall, executionResult, autoVerifyPlan)
                            : PendingWriteStep.CreateAwaitingVerification(toolCall, executionResult, autoVerifyPlan);
                        var changeExecutedEvent = CreateChangeEvent(
                            AgentEventType.ChangeExecuted,
                            executedWriteStep,
                            "写入已执行，系统正在执行验证步骤。");
                        await TryRecordTaskChangeAsync(auditRun?.Id, changeExecutedEvent, "executed", cancellationToken)
                            .ConfigureAwait(false);
                        yield return changeExecutedEvent;

                        var autoVerifyOutcome = await ExecuteAutoVerifyAsync(
                                executedWriteStep,
                                writeStepUndoScope,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (autoVerifyOutcome.ToolCall != null)
                        {
                            yield return CreateToolStartedEvent(
                                autoVerifyOutcome.ToolCall,
                                autoVerifyOutcome.OperationDescription);
                            await TryRecordTaskToolAsync(
                                    auditRun?.Id,
                                    autoVerifyOutcome.ToolCall,
                                    autoVerifyOutcome.Result,
                                    autoVerifyOutcome.OperationDescription,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            yield return CreateToolCompletedEvent(autoVerifyOutcome.ToolCall, autoVerifyOutcome.Result);
                        }

                        if (!autoVerifyOutcome.Passed)
                        {
                            pendingWriteStep = executedWriteStep.MarkRepairRequired(autoVerifyOutcome.FailureMessage);
                            if (writeStepUndoScope != null)
                            {
                                writeStepUndoScope.Rollback();
                                writeStepRolledBack = true;
                            }

                            if (todoWriteStepStarted && _todoManager != null)
                            {
                                currentTodoBoard = await _todoManager
                                    .RollbackCurrentWriteStepAsync(
                                        documentPath,
                                        pendingWriteStep.LastFailureMessage,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                yield return CreateTodoBoardSnapshotEvent(
                                    AgentEventType.TodoBoardUpdated,
                                    currentTodoBoard,
                                    _todoManager,
                                    TodoBoardUpdateKind.RollbackRestored,
                                    "当前写步骤已回退，Todo Board 已恢复到最近可信检查点。");
                            }

                            await AppendAutoVerifyObservationAsync(
                                    documentPath,
                                    messages,
                                    executedWriteStep,
                                    autoVerifyOutcome,
                                    AutoVerifyObservationDisposition.RolledBack,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            var verificationFailedEvent = CreateChangeEvent(
                                AgentEventType.ChangeVerificationFailed,
                                pendingWriteStep,
                                pendingWriteStep.LastFailureMessage,
                                autoVerifyOutcome.Result == null ? string.Empty : autoVerifyOutcome.Result.Output);
                            await TryRecordTaskChangeAsync(auditRun?.Id, verificationFailedEvent, "verification_failed", cancellationToken)
                                .ConfigureAwait(false);
                            yield return verificationFailedEvent;

                            if (pendingWriteStep.RepairAttempts >= WriteRepairAttemptLimit)
                            {
                                var pauseMessage = "当前写步骤已连续失败 3 次，系统已回退当前步骤并暂停。你可以继续尝试、跳过此步骤，或停止本次任务。";
                                if (safeOptions.Mode == AgentMode.Agent && runStarted && _todoManager != null)
                                {
                                    currentTodoBoard = await _todoManager
                                        .MarkRunPausedAsync(
                                            documentPath,
                                            TodoBoardRunOutcome.RolledBack,
                                            pauseMessage,
                                            CancellationToken.None)
                                    .ConfigureAwait(false);
                                    runPaused = true;
                                    auditCompletion = CreateTaskRunCompletion(
                                        TaskRunStatus.Paused,
                                        pauseMessage,
                                        completedStepsForAudit,
                                        totalStepsForAudit);
                                    yield return CreateTodoBoardPausedEvent(
                                        currentTodoBoard,
                                        _todoManager,
                                        pauseMessage);
                                }

                                if (writeStepUndoScope != null)
                                {
                                    writeStepUndoScope.Dispose();
                                    writeStepUndoScope = null;
                                }

                                yield break;
                            }
                        }
                        else
                        {
                            if (writeStepUndoScope != null)
                            {
                                writeStepUndoScope.Commit();
                                writeStepCommitted = true;
                            }

                            if (todoWriteStepStarted && _todoManager != null)
                            {
                                currentTodoBoard = await _todoManager
                                    .MarkWriteStepCommittedAsync(
                                        documentPath,
                                        executedWriteStep.OperationDescription,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            await AppendAutoVerifyObservationAsync(
                                    documentPath,
                                    messages,
                                    executedWriteStep,
                                    autoVerifyOutcome,
                                    AutoVerifyObservationDisposition.Committed,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            pendingWriteStep = null;
                            if (IsDocumentWriteTool(toolCall.Name))
                            {
                                successfulDocumentWriteThisIteration = true;
                                hasSuccessfulDocumentWriteOccurredInRun = true;
                            }

                            var changeAppliedEvent = CreateChangeEvent(
                                AgentEventType.ChangeApplied,
                                executedWriteStep,
                                "已通过验证步骤确认改动生效。",
                                autoVerifyOutcome.Result == null ? string.Empty : autoVerifyOutcome.Result.Output);
                            completedStepsForAudit++;
                            await TryRecordTaskChangeAsync(auditRun?.Id, changeAppliedEvent, "verified", cancellationToken)
                                .ConfigureAwait(false);
                            yield return changeAppliedEvent;
                            consecutiveFailures = 0;
                        }

                        shouldContinueWithNextAssistantTurn = true;
                        remainingToolCallsStartIndex = toolCallIndex + 1;
                        remainingToolCallsReason = "当前轮次已进入写后自动验证，剩余工具调用已跳过。";
                        break;
                    }

                    consecutiveFailures = 0;
                    if (pendingWriteStep != null)
                    {
                        shouldContinueWithNextAssistantTurn = true;
                        remainingToolCallsStartIndex = toolCallIndex + 1;
                        remainingToolCallsReason = "当前轮次仍需先完成写步骤验证，剩余工具调用已跳过。";
                        break;
                    }
                }
                else
                {
                    if (isDocumentWriteTool)
                    {
                        pendingWriteStep = pendingWriteStep != null
                            ? pendingWriteStep.RegisterWriteFailure(toolCall, executionResult, operationDescription)
                            : PendingWriteStep.CreateRepairRequired(toolCall, executionResult, operationDescription);
                        if (writeStepUndoScope != null)
                        {
                            writeStepUndoScope.Rollback();
                            writeStepRolledBack = true;
                        }

                        if (todoWriteStepStarted && _todoManager != null)
                        {
                            currentTodoBoard = await _todoManager
                                .RollbackCurrentWriteStepAsync(
                                    documentPath,
                                    pendingWriteStep.LastFailureMessage,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            yield return CreateTodoBoardSnapshotEvent(
                                AgentEventType.TodoBoardUpdated,
                                currentTodoBoard,
                                _todoManager,
                                TodoBoardUpdateKind.RollbackRestored,
                                "当前写步骤已回退，Todo Board 已恢复到最近可信检查点。");
                        }

                        var repairRequiredEvent = CreateChangeEvent(
                            AgentEventType.ChangeRepairRequired,
                            pendingWriteStep,
                            pendingWriteStep.LastFailureMessage,
                            executionResult.Output);
                        await TryRecordTaskChangeAsync(auditRun?.Id, repairRequiredEvent, "repair_required", cancellationToken)
                            .ConfigureAwait(false);
                        yield return repairRequiredEvent;

                        if (pendingWriteStep.RepairAttempts >= WriteRepairAttemptLimit)
                        {
                            var pauseMessage = "当前写步骤已连续失败 3 次，系统已回退当前步骤并暂停。你可以继续尝试、跳过此步骤，或停止本次任务。";
                            if (safeOptions.Mode == AgentMode.Agent && runStarted && _todoManager != null)
                            {
                                currentTodoBoard = await _todoManager
                                    .MarkRunPausedAsync(
                                        documentPath,
                                        TodoBoardRunOutcome.RolledBack,
                                        pauseMessage,
                                        CancellationToken.None)
                                .ConfigureAwait(false);
                                runPaused = true;
                                auditCompletion = CreateTaskRunCompletion(
                                    TaskRunStatus.Paused,
                                    pauseMessage,
                                    completedStepsForAudit,
                                    totalStepsForAudit);
                                yield return CreateTodoBoardPausedEvent(
                                    currentTodoBoard,
                                    _todoManager,
                                    pauseMessage);
                            }

                            if (writeStepUndoScope != null)
                            {
                                writeStepUndoScope.Dispose();
                                writeStepUndoScope = null;
                            }

                            yield break;
                        }

                        shouldContinueWithNextAssistantTurn = true;
                        remainingToolCallsStartIndex = toolCallIndex + 1;
                        remainingToolCallsReason = "当前写步骤执行失败，系统已进入修复状态，剩余工具调用已跳过。";
                        break;
                    }

                    RecordConsecutiveFailure(
                        ref consecutiveFailures,
                        ref lastFailureSummary,
                        toolCall.Name,
                        executionResult.Output);
                    if (consecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                        yield return CreateCircuitBreakerEvent(lastFailureSummary);
                        yield break;
                    }

                    if (pendingWriteStep != null)
                    {
                        shouldContinueWithNextAssistantTurn = true;
                        remainingToolCallsStartIndex = toolCallIndex + 1;
                        remainingToolCallsReason = "当前轮次已停在待修复状态，剩余工具调用已跳过。";
                        break;
                    }
                }

                if (writeStepUndoScope != null)
                {
                    if (!writeStepCommitted && !writeStepRolledBack)
                    {
                        writeStepUndoScope.Rollback();
                        writeStepRolledBack = true;
                        if (todoWriteStepStarted && _todoManager != null)
                        {
                            currentTodoBoard = await _todoManager
                                .RollbackCurrentWriteStepAsync(
                                    documentPath,
                                    interruptedReason,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }

                    writeStepUndoScope.Dispose();
                }
            }

            if (_todoManager != null && safeOptions.Mode == AgentMode.Agent && currentTodoBoard != null)
            {
                if (todoWriteThisIteration)
                {
                    currentTodoBoard = await _todoManager
                        .GetBoardAsync(documentPath, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (toolCalls.Count > 0)
                {
                    currentTodoBoard = await _todoManager
                        .RecordRoundWithoutTodoWriteAsync(
                            documentPath,
                            hasEffectiveExecutionRoundThisIteration,
                            successfulDocumentWriteThisIteration,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (_todoReminderService != null)
                    {
                        reminderDecision = _todoReminderService.BuildDecision(
                            currentTodoBoard,
                            hasSuccessfulDocumentWriteOccurredInRun);
                    }
                }
            }

            if (shouldContinueWithNextAssistantTurn)
            {
                if (remainingToolCallsStartIndex >= 0 && remainingToolCallsStartIndex < toolCalls.Count)
                {
                    await AppendSkippedRemainingToolCallsAsync(
                            documentPath,
                            messages,
                            toolCalls,
                            remainingToolCallsStartIndex,
                            remainingToolCallsReason,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (reminderDecision != null && reminderDecision.ShouldInject)
                {
                    messages.Add(new AgentMessage
                    {
                        Role = "user",
                        Content = reminderDecision.Message
                    });

                    currentTodoBoard = await _todoManager
                        .MarkReminderInjectedAsync(
                            documentPath,
                            reminderDecision.IsHighPriority,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var reminderStats = _todoManager.BuildStats(currentTodoBoard);
                    yield return new AgentEvent
                    {
                        Type = AgentEventType.TodoReminderInjected,
                        Message = reminderDecision.Message,
                        BoardJson = _todoManager.SerializeBoard(currentTodoBoard),
                        CurrentTodoId = reminderStats.CurrentTodoId,
                        CompletedSteps = reminderStats.HandledCount,
                        TotalSteps = reminderStats.TotalCount,
                        TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(TodoBoardUpdateKind.Reminder)
                    };
                }

                continue;
            }

            if (reminderDecision != null && reminderDecision.ShouldInject)
            {
                messages.Add(new AgentMessage
                {
                    Role = "user",
                    Content = reminderDecision.Message
                });

                currentTodoBoard = await _todoManager
                    .MarkReminderInjectedAsync(
                        documentPath,
                        reminderDecision.IsHighPriority,
                        cancellationToken)
                    .ConfigureAwait(false);

                var reminderStats = _todoManager.BuildStats(currentTodoBoard);
                yield return new AgentEvent
                {
                    Type = AgentEventType.TodoReminderInjected,
                    Message = reminderDecision.Message,
                    BoardJson = _todoManager.SerializeBoard(currentTodoBoard),
                    CurrentTodoId = reminderStats.CurrentTodoId,
                    CompletedSteps = reminderStats.HandledCount,
                    TotalSteps = reminderStats.TotalCount,
                    TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(TodoBoardUpdateKind.Reminder)
                };
            }
        }

        if (pendingWriteStep != null)
        {
            var pauseMessage = "当前写步骤尚未修复，系统已回退当前步骤并暂停。你可以继续尝试、跳过此步骤，或停止本次任务。";
            var pendingWriteStateEvent = CreatePendingWriteStateEvent(pendingWriteStep);
            await TryRecordTaskChangeAsync(
                    auditRun?.Id,
                    pendingWriteStateEvent,
                    pendingWriteStateEvent.Type == AgentEventType.ChangeUnverified ? "unverified" : "repair_required",
                    cancellationToken)
                .ConfigureAwait(false);
            yield return pendingWriteStateEvent;
            if (safeOptions.Mode == AgentMode.Agent && runStarted && _todoManager != null)
            {
                currentTodoBoard = await _todoManager
                    .MarkRunPausedAsync(
                        documentPath,
                        TodoBoardRunOutcome.RolledBack,
                        pauseMessage,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                runPaused = true;
                auditCompletion = CreateTaskRunCompletion(
                    TaskRunStatus.Paused,
                    pauseMessage,
                    completedStepsForAudit,
                    totalStepsForAudit);
                yield return CreateTodoBoardPausedEvent(
                    currentTodoBoard,
                    _todoManager,
                    pauseMessage);
            }

            yield break;
        }

        if (iteration >= maxIterations)
        {
            var maxIterationsMessage = BuildMaxIterationsMessage(safeOptions.Mode, maxIterations);
            yield return CreateMaxIterationsReachedEvent(safeOptions.Mode, maxIterations, maxIterationsMessage);

            if (safeOptions.Mode == AgentMode.Agent && runStarted && _todoManager != null)
            {
                currentTodoBoard = await _todoManager
                    .MarkRunPausedAsync(documentPath, maxIterationsMessage, CancellationToken.None)
                    .ConfigureAwait(false);
                runPaused = true;
                auditCompletion = CreateTaskRunCompletion(
                    TaskRunStatus.Paused,
                    maxIterationsMessage,
                    completedStepsForAudit,
                    totalStepsForAudit);
                yield return CreateTodoBoardPausedEvent(
                    currentTodoBoard,
                    _todoManager,
                    maxIterationsMessage);
            }

            yield break;
        }

        completedSuccessfully = true;
    }
    finally
    {
        if (runStarted
            && !completedSuccessfully
            && !runPaused
            && interruptedOutcome != TodoBoardRunOutcome.None
            && _todoManager != null
            && safeOptions.Mode == AgentMode.Agent)
        {
            await _todoManager
                .MarkRunInterruptedAsync(documentPath, interruptedOutcome, interruptedReason, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (auditRun != null && !auditRunCompleted)
        {
            if (auditCompletion == null)
            {
                auditCompletion = ResolveTaskRunCompletion(
                    completedSuccessfully,
                    runPaused,
                    interruptedOutcome,
                    interruptedReason,
                    completedStepsForAudit,
                    totalStepsForAudit);
            }

            await TryCompleteTaskRunAsync(auditRun, auditCompletion, CancellationToken.None)
                .ConfigureAwait(false);
            auditRunCompleted = true;
        }
    }

    if (runStarted && completedSuccessfully && _todoManager != null && safeOptions.Mode == AgentMode.Agent)
    {
        await _todoManager
            .MarkRunSucceededAndDeleteAsync(documentPath, CancellationToken.None)
            .ConfigureAwait(false);
    }

    if (!completedSuccessfully)
    {
        yield break;
    }

    yield return new AgentEvent
    {
        Type = AgentEventType.TaskCompleted,
        Citations = BuildCitations(finalAssistantMessage?.Content, citationRegistry),
        Message = string.Empty
    };
}

        private async Task<TaskRunRecord> TryStartTaskRunAsync(
            string documentPath,
            string userInput,
            AgentRunOptions options,
            CancellationToken cancellationToken)
        {
            if (_taskHistoryStore == null)
            {
                return null;
            }

            try
            {
                return await _taskHistoryStore
                    .StartRunAsync(
                        new TaskRunStartRequest
                        {
                            DocumentPath = documentPath,
                            UserGoal = userInput ?? string.Empty,
                            Mode = ToTaskHistoryMode(options == null ? AgentMode.Ask : options.Mode),
                            PermissionMode = ToTaskHistoryPermissionMode(ResolvePermissionMode(options)),
                            Model = options == null ? string.Empty : options.Model ?? string.Empty,
                            TotalSteps = ResolveTotalSteps(options)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "创建任务历史运行记录失败，主流程将继续。");
                return null;
            }
        }

        private async Task TryRecordTaskToolAsync(
            string taskRunId,
            ToolCall toolCall,
            ToolCallResult result,
            string operationDescription,
            CancellationToken cancellationToken)
        {
            if (_taskHistoryStore == null || string.IsNullOrWhiteSpace(taskRunId) || toolCall == null)
            {
                return;
            }

            try
            {
                await _taskHistoryStore
                    .RecordToolAsync(
                        taskRunId,
                        new TaskToolRecord
                        {
                            ToolCallId = toolCall.Id ?? string.Empty,
                            ToolName = toolCall.Name ?? string.Empty,
                            OperationDescription = string.IsNullOrWhiteSpace(operationDescription)
                                ? toolCall.Description ?? string.Empty
                                : operationDescription,
                            RawInput = toolCall.Input ?? string.Empty,
                            Output = result == null ? string.Empty : result.Output ?? string.Empty,
                            Success = result != null && result.Success,
                            CreatedAtUtc = DateTimeOffset.UtcNow
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "记录任务工具审计失败。TaskRunId={TaskRunId}, ToolName={ToolName}", taskRunId, toolCall.Name);
            }
        }

        private async Task TryRecordTaskChangeAsync(
            string taskRunId,
            AgentEvent changeEvent,
            string status,
            CancellationToken cancellationToken)
        {
            if (_taskHistoryStore == null || string.IsNullOrWhiteSpace(taskRunId) || changeEvent == null)
            {
                return;
            }

            try
            {
                await _taskHistoryStore
                    .RecordChangeAsync(
                        taskRunId,
                        new TaskChangeRecord
                        {
                            ToolCallId = changeEvent.ToolCallId ?? string.Empty,
                            ToolName = changeEvent.ToolName ?? string.Empty,
                            OperationDescription = changeEvent.OperationDescription ?? string.Empty,
                            AffectedParagraphs = changeEvent.AffectedParagraphs ?? new int[0],
                            Status = status ?? string.Empty,
                            Message = string.IsNullOrWhiteSpace(changeEvent.Message)
                                ? changeEvent.ToolOutput ?? string.Empty
                                : changeEvent.Message,
                            CreatedAtUtc = DateTimeOffset.UtcNow
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "记录任务改动审计失败。TaskRunId={TaskRunId}, Status={Status}", taskRunId, status);
            }
        }

        private async Task TryCompleteTaskRunAsync(
            TaskRunRecord auditRun,
            TaskRunCompletion completion,
            CancellationToken cancellationToken)
        {
            if (_taskHistoryStore == null || auditRun == null || string.IsNullOrWhiteSpace(auditRun.Id))
            {
                return;
            }

            try
            {
                await _taskHistoryStore
                    .CompleteRunAsync(auditRun.Id, completion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "完成任务历史运行记录失败。TaskRunId={TaskRunId}", auditRun.Id);
            }
        }

        private async Task AppendToolResultAsync(
            string documentPath,
            IList<AgentMessage> messages,
            ToolCall toolCall,
            ToolCallResult result,
            CancellationToken cancellationToken,
            string taskRunId = null,
            string operationDescription = null)
        {
            await _conversationStore
                .AppendToolResultAsync(
                    documentPath,
                    toolCall.Id,
                    toolCall.Name,
                    toolCall.Input ?? string.Empty,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);

            await TryRecordTaskToolAsync(
                    taskRunId,
                    toolCall,
                    result,
                    operationDescription,
                    cancellationToken)
                .ConfigureAwait(false);

            messages.Add(new AgentMessage
            {
                Role = "tool",
                ToolCallId = toolCall.Id,
                Name = toolCall.Name,
                Content = result.Output ?? string.Empty,
                ToolName = toolCall.Name,
                RawToolInput = toolCall.Input ?? string.Empty,
                ToolSuccess = result.Success
            });
        }

        private static ConversationCompressionContext CreateCompressionContext(
            AgentRunOptions options,
            string documentPath,
            DocumentContext documentContext,
            TodoBoard currentTodoBoard,
            PendingWriteStep pendingWriteStep,
            IReadOnlyList<AgentMessage> messages)
        {
            var recentInternalObservations = messages == null
                ? new List<AgentMessage>()
                : messages
                    .Where(message => message != null && message.IsInternalObservation)
                    .Reverse()
                    .Take(5)
                    .Select(CloneMessage)
                    .Reverse()
                    .ToList();

            var latestRealUserMessage = messages == null
                ? null
                : messages.LastOrDefault(message =>
                    message != null
                    && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                    && !message.IsInternalObservation);

            return new ConversationCompressionContext
            {
                Mode = options == null ? AgentMode.Ask : options.Mode,
                DocumentPath = documentPath ?? string.Empty,
                CurrentUserGoal = latestRealUserMessage == null
                    ? string.Empty
                    : latestRealUserMessage.Content ?? string.Empty,
                CurrentTodoBoard = currentTodoBoard,
                ActivePlan = options == null ? null : options.ActivePlan,
                PendingWriteStep = CreatePendingWriteStepSnapshot(pendingWriteStep),
                DocumentContext = documentContext,
                RecentInternalObservations = recentInternalObservations
            };
        }

        private static PendingWriteStepSnapshot CreatePendingWriteStepSnapshot(PendingWriteStep pendingWriteStep)
        {
            if (pendingWriteStep == null)
            {
                return null;
            }

            return new PendingWriteStepSnapshot
            {
                ToolCallId = pendingWriteStep.ToolCallId,
                ToolName = pendingWriteStep.ToolName,
                AffectedParagraphs = pendingWriteStep.AffectedParagraphs,
                OperationDescription = pendingWriteStep.OperationDescription,
                State = pendingWriteStep.State.ToString(),
                RepairAttempts = pendingWriteStep.RepairAttempts,
                LastFailureMessage = pendingWriteStep.LastFailureMessage,
                VerificationToolName = pendingWriteStep.VerificationToolName,
                VerificationOperationDescription = pendingWriteStep.VerificationOperationDescription,
                VerificationFailureReason = pendingWriteStep.VerificationFailureReason
            };
        }

        private async Task AppendInternalObservationAsync(
            string documentPath,
            IList<AgentMessage> messages,
            string content,
            CancellationToken cancellationToken)
        {
            // 系统内部观察不是模型发起的工具结果，必须用普通消息进入上下文，避免产生孤立 tool 消息。
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var message = new AgentMessage
            {
                Role = "user",
                Content = content.Trim(),
                IsInternalObservation = true,
                InternalObservationKind = "auto_verify_result"
            };

            await _conversationStore
                .AppendUserMessageAsync(documentPath, message, cancellationToken)
                .ConfigureAwait(false);

            messages.Add(CloneMessage(message));
        }

        private async Task AppendSkippedRemainingToolCallsAsync(
            string documentPath,
            IList<AgentMessage> messages,
            IReadOnlyList<ToolCall> toolCalls,
            int startIndex,
            string reason,
            CancellationToken cancellationToken)
        {
            if (toolCalls == null || startIndex < 0 || startIndex >= toolCalls.Count)
            {
                return;
            }

            for (var index = startIndex; index < toolCalls.Count; index++)
            {
                var skippedToolCall = toolCalls[index];
                var skippedResult = ToolCallResult.Skipped(
                    skippedToolCall.Name,
                    string.IsNullOrWhiteSpace(reason)
                        ? "当前轮次已提前结束，剩余工具调用已跳过。"
                        : reason);

                await AppendToolResultAsync(
                        documentPath,
                        messages,
                        skippedToolCall,
                        skippedResult,
                        cancellationToken)
                    .ConfigureAwait(false);

                Log.Information(
                    "已为剩余工具调用补齐 skipped 结果。ToolCallId={ToolCallId}, ToolName={ToolName}, Reason={Reason}",
                    skippedToolCall.Id,
                    skippedToolCall.Name,
                    reason);
            }
        }

        private string BuildSystemPrompt(AgentRunOptions options, DocumentContext documentContext, TodoBoard todoBoard)
        {
            var prompt = _systemPromptBuilder.Build(options.Mode);
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("--- DOCUMENT CONTEXT ---");
            contextBuilder.AppendLine(
                $"Document: {documentContext.DocumentName} ({documentContext.Complexity}: {documentContext.WordCount} words, {documentContext.ParagraphCount} paragraphs)");
            contextBuilder.AppendLine($"Path: {documentContext.DocumentPath}");
            contextBuilder.AppendLine($"Pages: {documentContext.CurrentPageNumber} / {documentContext.TotalPages}");
            contextBuilder.AppendLine($"Cursor: Paragraph #{documentContext.CursorParagraphIndex}");
            if (documentContext.HasSelection)
            {
                contextBuilder.AppendLine($"Selected: \"{documentContext.SelectedText}\" (Paragraph #{documentContext.SelectionParagraphIndex})");
            }

            contextBuilder.AppendLine(
                $"Stats: tables={documentContext.TableCount}, images={documentContext.ImageCount}, annotations={documentContext.AnnotationCount}");
            contextBuilder.AppendLine(
                $"Status: {(documentContext.DocumentStatus == null ? string.Empty : documentContext.DocumentStatus.GetUserFriendlyMessage())}");
            if (documentContext.DocumentStatus != null && documentContext.DocumentStatus.IsTrackChangesEnforced)
            {
                contextBuilder.AppendLine("Notice: 当前文档已启用修订模式，写入会以修订痕迹呈现，不应把它误判为失败。");
            }
            if (documentContext.Headings != null && documentContext.Headings.Count > 0)
            {
                contextBuilder.AppendLine("Document Outline:");
                foreach (var heading in documentContext.Headings)
                {
                    contextBuilder.AppendLine(
                        $"{new string(' ', Math.Max(0, (heading.Level - 1) * 2))}- {heading.Text} (¶{heading.ParagraphIndex})");
                }
            }

            if (todoBoard != null && _todoManager != null)
            {
                contextBuilder.AppendLine();
                contextBuilder.AppendLine(_todoManager.BuildPromptBlock(todoBoard));
                contextBuilder.AppendLine("Notice: 复杂任务应持续维护 todo board。计划变化时，先更新任务板再继续执行。");
            }

            var finalPrompt = string.IsNullOrWhiteSpace(prompt)
                ? contextBuilder.ToString()
                : prompt + Environment.NewLine + Environment.NewLine + contextBuilder;

            if (!string.IsNullOrWhiteSpace(options.CustomSystemInstructions))
            {
                finalPrompt += Environment.NewLine
                    + Environment.NewLine
                    + "--- USER CUSTOM INSTRUCTIONS ---"
                    + Environment.NewLine
                    + options.CustomSystemInstructions;
            }

            if (!options.EnableToolCalling)
            {
                finalPrompt += Environment.NewLine
                    + Environment.NewLine
                    + "--- MODEL CAPABILITY NOTICE ---"
                    + Environment.NewLine
                    + "当前模型不支持工具调用，你无法读取或检索 Word 文档内容。"
                    + Environment.NewLine
                    + "你必须明确说明这一限制，不能假装已经访问过文档。";
            }

            return finalPrompt;
        }

        private static bool TryGetTodoToolMetadata(ToolCallResult result, out TodoToolMetadata metadata)
        {
            metadata = result == null ? null : result.Metadata as TodoToolMetadata;
            return metadata != null && !string.IsNullOrWhiteSpace(metadata.BoardJson);
        }

        private static TodoBoard DeserializeTodoBoard(string boardJson)
        {
            if (string.IsNullOrWhiteSpace(boardJson))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<TodoBoard>(boardJson);
            }
            catch
            {
                return null;
            }
        }

        private static string SerializeCamelCase(object value)
        {
            return JsonConvert.SerializeObject(
                value,
                new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                    NullValueHandling = NullValueHandling.Ignore
                });
        }

        private static AgentEvent CreateTodoBoardEvent(
            AgentEventType eventType,
            TodoToolMetadata metadata,
            TodoBoardUpdateKind updateKind,
            string message)
        {
            return new AgentEvent
            {
                Type = eventType,
                Message = message ?? string.Empty,
                BoardJson = metadata == null ? string.Empty : metadata.BoardJson ?? string.Empty,
                CurrentTodoId = metadata == null ? string.Empty : metadata.CurrentTodoId ?? string.Empty,
                CompletedSteps = metadata == null ? 0 : metadata.CompletedSteps,
                TotalSteps = metadata == null ? 0 : metadata.TotalSteps,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(updateKind)
            };
        }

        private static AgentEvent CreateTodoBoardReadyEvent(TodoBoard board, TodoManager todoManager, string message)
        {
            var stats = todoManager == null ? new TodoBoardStats() : todoManager.BuildStats(board);
            return new AgentEvent
            {
                Type = AgentEventType.TodoBoardReady,
                Message = message ?? "当前 Todo Board 已就绪。",
                BoardJson = todoManager == null ? string.Empty : todoManager.SerializeBoard(board),
                CurrentTodoId = stats.CurrentTodoId,
                CompletedSteps = stats.HandledCount,
                TotalSteps = stats.TotalCount,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(TodoBoardUpdateKind.Ready)
            };
        }

        private static AgentEvent CreateTodoBoardSnapshotEvent(
            AgentEventType eventType,
            TodoBoard board,
            TodoManager todoManager,
            TodoBoardUpdateKind updateKind,
            string message)
        {
            var stats = todoManager == null ? new TodoBoardStats() : todoManager.BuildStats(board);
            return new AgentEvent
            {
                Type = eventType,
                Message = message ?? string.Empty,
                BoardJson = board == null || todoManager == null ? string.Empty : todoManager.SerializeBoard(board),
                CurrentTodoId = stats.CurrentTodoId,
                CompletedSteps = stats.HandledCount,
                TotalSteps = stats.TotalCount,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(updateKind)
            };
        }

        private static AgentEvent CreateTodoBoardRecoveryRequiredEvent(
            TodoBoardPreparationResult prepareResult,
            TodoManager todoManager,
            string recoveryRequestId)
        {
            var board = prepareResult == null ? null : prepareResult.Board;
            var stats = todoManager == null ? new TodoBoardStats() : todoManager.BuildStats(board);
            return new AgentEvent
            {
                Type = AgentEventType.TodoBoardRecoveryRequired,
                BoardJson = board == null || todoManager == null ? string.Empty : todoManager.SerializeBoard(board),
                CurrentTodoId = stats.CurrentTodoId,
                CompletedSteps = stats.HandledCount,
                TotalSteps = stats.TotalCount,
                RecoveryRequestId = recoveryRequestId ?? string.Empty,
                RecoveryReason = prepareResult == null ? string.Empty : prepareResult.RecoveryReason,
                LastRunOutcome = prepareResult == null ? string.Empty : prepareResult.LastRunOutcome.ToString(),
                LastErrorSummary = prepareResult == null ? string.Empty : prepareResult.LastErrorSummary,
                HasActivePlan = prepareResult != null && prepareResult.HasActivePlan,
                CanRecoverExisting = prepareResult == null || prepareResult.CanRecoverExisting,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(TodoBoardUpdateKind.RecoverySnapshot)
            };
        }

        private static AgentEvent CreateTodoBoardPausedEvent(
            TodoBoardPreparationResult prepareResult,
            TodoManager todoManager,
            string recoveryRequestId)
        {
            var board = prepareResult == null ? null : prepareResult.Board;
            var stats = todoManager == null ? new TodoBoardStats() : todoManager.BuildStats(board);
            return new AgentEvent
            {
                Type = AgentEventType.TodoBoardPaused,
                Message = prepareResult == null
                    ? "当前任务已暂停。"
                    : string.IsNullOrWhiteSpace(prepareResult.PauseReason)
                        ? "当前任务已暂停。"
                        : prepareResult.PauseReason,
                BoardJson = board == null || todoManager == null ? string.Empty : todoManager.SerializeBoard(board),
                CurrentTodoId = stats.CurrentTodoId,
                CompletedSteps = stats.HandledCount,
                TotalSteps = stats.TotalCount,
                RecoveryRequestId = recoveryRequestId ?? string.Empty,
                LastRunOutcome = prepareResult == null ? string.Empty : prepareResult.LastRunOutcome.ToString(),
                LastErrorSummary = prepareResult == null ? string.Empty : prepareResult.LastErrorSummary,
                HasActivePlan = prepareResult != null && prepareResult.HasActivePlan,
                CanRecoverExisting = prepareResult == null || prepareResult.CanRecoverExisting,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(TodoBoardUpdateKind.PausedSnapshot)
            };
        }

        private static AgentEvent CreateTodoBoardPausedEvent(
            TodoBoard board,
            TodoManager todoManager,
            string message)
        {
            var stats = todoManager == null ? new TodoBoardStats() : todoManager.BuildStats(board);
            return new AgentEvent
            {
                Type = AgentEventType.TodoBoardPaused,
                Message = message ?? "当前任务已暂停。",
                BoardJson = board == null || todoManager == null ? string.Empty : todoManager.SerializeBoard(board),
                CurrentTodoId = stats.CurrentTodoId,
                CompletedSteps = stats.HandledCount,
                TotalSteps = stats.TotalCount,
                LastRunOutcome = board == null ? string.Empty : board.LastRunOutcome.ToString(),
                LastErrorSummary = board == null ? string.Empty : board.LastErrorSummary,
                TodoBoardUpdateKind = ToTodoBoardUpdateKindValue(TodoBoardUpdateKind.PausedSnapshot)
            };
        }

        private static string ToTodoBoardUpdateKindValue(TodoBoardUpdateKind updateKind)
        {
            switch (updateKind)
            {
                case TodoBoardUpdateKind.Ready:
                    return "ready";
                case TodoBoardUpdateKind.ToolReadSync:
                    return "tool_read_sync";
                case TodoBoardUpdateKind.ToolWriteSync:
                    return "tool_write_sync";
                case TodoBoardUpdateKind.RollbackRestored:
                    return "rollback_restored";
                case TodoBoardUpdateKind.Reminder:
                    return "reminder";
                case TodoBoardUpdateKind.PausedSnapshot:
                    return "paused_snapshot";
                case TodoBoardUpdateKind.RecoverySnapshot:
                    return "recovery_snapshot";
                case TodoBoardUpdateKind.Unknown:
                default:
                    return "unknown";
            }
        }

        private static AgentEvent CreateMaxIterationsReachedEvent(
            AgentMode mode,
            int maxIterations,
            string message)
        {
            return new AgentEvent
            {
                Type = AgentEventType.MaxIterationsReached,
                Message = string.IsNullOrWhiteSpace(message)
                    ? BuildMaxIterationsMessage(mode, maxIterations)
                    : message
            };
        }

        private static string BuildMaxIterationsMessage(AgentMode mode, int maxIterations)
        {
            switch (mode)
            {
                case AgentMode.Agent:
                    return $"当前任务已达到本轮 {maxIterations} 轮预算上限，系统已暂停并保留 Todo Board。你可以继续尝试、跳过当前步骤，或停止本次任务。";
                case AgentMode.Plan:
                    return $"当前规划已达到本轮 {maxIterations} 轮预算上限，但尚未生成最终计划。你可以继续补充信息后再次规划。";
                case AgentMode.Ask:
                default:
                    return $"当前回答已达到本轮 {maxIterations} 轮预算上限，若仍需继续，可直接继续追问。";
            }
        }

        private static AgentEvent CreateToolStartedEvent(ToolCall toolCall, string operationDescription)
        {
            return new AgentEvent
            {
                Type = AgentEventType.ToolCallStarted,
                ToolCallId = toolCall == null ? string.Empty : toolCall.Id,
                ToolName = toolCall == null ? string.Empty : toolCall.Name,
                ToolInput = toolCall == null ? string.Empty : toolCall.Input ?? string.Empty,
                RequiresConfirmation = false,
                OperationDescription = operationDescription ?? string.Empty
            };
        }

        private static AgentEvent CreateToolCompletedEvent(ToolCall toolCall, ToolCallResult result)
{
    return new AgentEvent
    {
        Type = AgentEventType.ToolCallCompleted,
        ToolCallId = toolCall.Id,
        ToolName = toolCall.Name,
        ToolInput = toolCall.Input ?? string.Empty,
        ToolOutput = result.Output ?? string.Empty,
        ToolSuccess = result.Success,
        ParagraphRefs = result.ParagraphRefs,
        AffectedParagraphs = result.AffectedParagraphs,
        OperationDescription = result.OperationDescription ?? string.Empty
    };
}

        private static string DecorateToolOutput(
            string toolName,
            string output,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var trimmedOutput = output.TrimStart();
            if (!trimmedOutput.StartsWith("{", StringComparison.Ordinal)
                && !trimmedOutput.StartsWith("[", StringComparison.Ordinal))
            {
                return output;
            }

            try
            {
                var token = JToken.Parse(output);
                var rootObject = token as JObject;
                var discovered = new JArray();

                switch (toolName ?? string.Empty)
                {
                    case "read_section":
                        AttachRefsOnArray(
                            token["paragraphs"] as JArray,
                            "index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                    case "grep_document":
                        AttachRefsOnArray(
                            token["results"] as JArray,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        foreach (var item in (token["results"] as JArray ?? new JArray()).OfType<JObject>())
                        {
                            AttachRefsOnArray(
                                item["context_before"] as JArray,
                                "index",
                                "text",
                                documentPath,
                                citationRegistry,
                                paragraphToRef,
                                ref nextCitationRef,
                                discovered);
                            AttachRefsOnArray(
                                item["context_after"] as JArray,
                                "index",
                                "text",
                                documentPath,
                                citationRegistry,
                                paragraphToRef,
                                ref nextCitationRef,
                                discovered);
                        }
                        break;
                    case "probe_document":
                        AttachRefsOnArray(
                            token["outline"] as JArray,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        AttachRefOnObject(
                            token["selection"] as JObject,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                    case "get_selection_context":
                        AttachRefOnObject(
                            token["selection"] as JObject,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        AttachRefOnObject(
                            token["context"] as JObject,
                            "paragraph_index",
                            "paragraph_full",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                    case "read_annotations":
                        AttachRefsOnArray(
                            token["results"] as JArray,
                            "para_index",
                            "anchor_text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                }

                if (discovered.Count > 0 && rootObject != null)
                {
                    rootObject["citation_entries"] = discovered;
                }

                return (rootObject ?? token).ToString(Formatting.None);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "装饰工具输出的引用映射失败。ToolName={ToolName}", toolName);
                return output;
            }
        }

        private static void AttachRefsOnArray(
            JArray array,
            string paragraphPropertyName,
            string excerptPropertyName,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef,
            JArray discovered)
        {
            if (array == null)
            {
                return;
            }

            foreach (var item in array.OfType<JObject>())
            {
                AttachRefOnObject(
                    item,
                    paragraphPropertyName,
                    excerptPropertyName,
                    documentPath,
                    citationRegistry,
                    paragraphToRef,
                    ref nextCitationRef,
                    discovered);
            }
        }

        private static void AttachRefOnObject(
            JObject target,
            string paragraphPropertyName,
            string excerptPropertyName,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef,
            JArray discovered)
        {
            if (target == null)
            {
                return;
            }

            var paragraphIndex = target.Value<int?>(paragraphPropertyName);
            if (!paragraphIndex.HasValue || paragraphIndex.Value < 0)
            {
                return;
            }

            var excerpt = target.Value<string>(excerptPropertyName) ?? string.Empty;
            var refId = RegisterCitation(
                paragraphIndex.Value,
                excerpt,
                documentPath,
                citationRegistry,
                paragraphToRef,
                ref nextCitationRef);
            target["ref"] = refId;
            discovered.Add(new JObject
            {
                ["ref"] = refId,
                ["paragraphIndex"] = paragraphIndex.Value,
                ["excerpt"] = excerpt
            });
        }

        private static int RegisterCitation(
            int paragraphIndex,
            string excerpt,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef)
        {
            if (paragraphToRef.TryGetValue(paragraphIndex, out var existingRef))
            {
                return existingRef;
            }

            var refId = nextCitationRef++;
            paragraphToRef[paragraphIndex] = refId;
            citationRegistry[refId] = new CitationEntry
            {
                Ref = refId,
                ParagraphIndex = paragraphIndex,
                Excerpt = excerpt,
                DocumentPath = documentPath
            };

            return refId;
        }

        private static List<CitationEntry> BuildCitations(
            string assistantContent,
            IReadOnlyDictionary<int, CitationEntry> citationRegistry)
        {
            var citations = new List<CitationEntry>();
            if (string.IsNullOrWhiteSpace(assistantContent) || citationRegistry == null || citationRegistry.Count == 0)
            {
                return citations;
            }

            foreach (Match match in Regex.Matches(assistantContent, @"\[(\d+)\]"))
            {
                if (!int.TryParse(match.Groups[1].Value, out var refId))
                {
                    continue;
                }

                if (!citationRegistry.TryGetValue(refId, out var citation))
                {
                    continue;
                }

                if (citations.Any(item => item.Ref == refId))
                {
                    continue;
                }

                citations.Add(citation);
            }

            return citations;
        }

        private static void RecordConsecutiveFailure(
            ref int consecutiveFailures,
            ref string lastFailureSummary,
            string toolName,
            string toolOutput)
        {
            consecutiveFailures++;
            lastFailureSummary = BuildFailureSummary(toolName, toolOutput);
        }

        private static AgentEvent CreateCircuitBreakerEvent(string lastFailureSummary)
        {
            var message = "工具已连续失败 3 次，系统为防止误操作已停止本次任务。";
            if (!string.IsNullOrWhiteSpace(lastFailureSummary))
            {
                message += Environment.NewLine
                    + "最近一次失败大致原因："
                    + lastFailureSummary
                    + Environment.NewLine
                    + "建议：检查任务范围、选区或指令后重新发起；如果是文档状态或权限问题，请先处理对应限制。";
            }

            return new AgentEvent
            {
                Type = AgentEventType.Error,
                Message = message
            };
        }

        private static string BuildFailureSummary(string toolName, string toolOutput)
        {
            var normalizedToolName = string.IsNullOrWhiteSpace(toolName)
                ? "未知工具"
                : toolName.Trim();
            var normalizedOutput = NormalizeFailureText(toolOutput);
            return string.IsNullOrWhiteSpace(normalizedOutput)
                ? $"工具 {normalizedToolName} 未返回明确错误详情。"
                : $"工具 {normalizedToolName}：{Truncate(normalizedOutput, 240)}";
        }

        private static string NormalizeFailureText(string toolOutput)
        {
            if (string.IsNullOrWhiteSpace(toolOutput))
            {
                return string.Empty;
            }

            var text = Regex.Replace(toolOutput, @"\[(ERROR in [^\]]+|PERMISSION DENIED|SKIPPED)\]\s*", string.Empty);
            text = Regex.Replace(text, @"Tool '[^']+' (was blocked|is not allowed in current mode|was skipped by user)\.?", string.Empty);
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static bool IsTodoToolName(string toolName)
        {
            return string.Equals(toolName, "todo_read", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "todo_write", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDocumentWriteTool(string toolName)
        {
            return string.Equals(toolName, "patch_range", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "execute_script", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVerificationTool(string toolName)
        {
            return string.Equals(toolName, "verify_script", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRepairProbeTool(string toolName)
        {
            return string.Equals(toolName, "read_script", StringComparison.OrdinalIgnoreCase);
        }

        private static AutoVerifyPlan BuildAutoVerifyPlan(string toolName, JObject parsedInput)
        {
            switch ((toolName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "patch_range":
                    return BuildPatchRangeAutoVerifyPlan(parsedInput);
                case "execute_script":
                    return BuildExecuteScriptAutoVerifyPlan(parsedInput);
                default:
                    return AutoVerifyPlan.Unsupported("当前工具类型不支持系统写后验证。");
            }
        }

        private static AutoVerifyPlan BuildPatchRangeAutoVerifyPlan(JObject parsedInput)
        {
            if (parsedInput == null)
            {
                return AutoVerifyPlan.Unsupported("patch_range 缺少结构化输入，无法生成写后验证步骤。");
            }

            PatchRangeRequest request;
            using (var inputDocument = JsonDocument.Parse(parsedInput.ToString(Formatting.None)))
            {
                request = PatchRangeRequest.Parse(inputDocument.RootElement);
            }

            if (request.Operations.Count == 0)
            {
                return AutoVerifyPlan.Unsupported("patch_range 未提供可验证的 operations。");
            }

            var checks = new JArray();
            foreach (var operation in request.Operations)
            {
                switch ((operation.Type ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "replace_text":
                        checks.Add(new JObject
                        {
                            ["type"] = "text_equals",
                            ["paragraph_index"] = operation.ParagraphIndex,
                            ["expected"] = NormalizeAutoVerifyText(operation.Text)
                        });
                        break;
                    case "insert_paragraph_after":
                        checks.Add(new JObject
                        {
                            ["type"] = "paragraph_exists",
                            ["paragraph_index"] = operation.ParagraphIndex + 1,
                            ["should_exist"] = true
                        });
                        checks.Add(new JObject
                        {
                            ["type"] = "text_equals",
                            ["paragraph_index"] = operation.ParagraphIndex + 1,
                            ["expected"] = NormalizeAutoVerifyText(operation.Text)
                        });
                        if (!string.IsNullOrWhiteSpace(operation.Style))
                        {
                            checks.Add(new JObject
                            {
                                ["type"] = "style_equals",
                                ["paragraph_index"] = operation.ParagraphIndex + 1,
                                ["expected"] = operation.Style
                            });
                        }

                        break;
                    case "set_paragraph_style":
                        if (string.IsNullOrWhiteSpace(operation.Style))
                        {
                            return AutoVerifyPlan.Unsupported("set_paragraph_style 缺少 style，无法生成写后验证步骤。");
                        }

                        checks.Add(new JObject
                        {
                            ["type"] = "style_equals",
                            ["paragraph_index"] = operation.ParagraphIndex,
                            ["expected"] = operation.Style
                        });
                        break;
                    case "delete_paragraph":
                        return AutoVerifyPlan.Unsupported("delete_paragraph 暂不支持可靠的系统写后验证，请改用 execute_script 并显式提供 verify_code。");
                    default:
                        return AutoVerifyPlan.Unsupported("存在当前版本不支持系统写后验证的 patch_range 操作类型：" + operation.Type);
                }
            }

            if (checks.Count == 0)
            {
                return AutoVerifyPlan.Unsupported("当前写步骤未生成任何可执行的验证脚本。");
            }

            return AutoVerifyPlan.Supported(
                "verify_script",
                BuildPatchRangeAutoVerifyInput(checks),
                "系统正在执行当前写步骤的验证。");
        }

        private static AutoVerifyPlan BuildExecuteScriptAutoVerifyPlan(JObject parsedInput)
        {
            if (parsedInput == null)
            {
                return AutoVerifyPlan.Unsupported("execute_script 缺少结构化输入，无法生成写后验证步骤。");
            }

            var verifyCode = parsedInput.Value<string>("verify_code");
            if (string.IsNullOrWhiteSpace(verifyCode))
            {
                return AutoVerifyPlan.Unsupported("execute_script 未提供 verify_code，系统无法执行当前写步骤的验证。");
            }

            return AutoVerifyPlan.Supported(
                "verify_script",
                new JObject
                {
                    ["description"] = "验证当前脚本写步骤是否生效。",
                    ["code"] = verifyCode
                }.ToString(Formatting.None),
                "系统正在执行当前脚本写步骤的验证。");
        }

        private async Task<AutoVerifyOutcome> ExecuteAutoVerifyAsync(
            PendingWriteStep pendingWriteStep,
            IUndoScope undoScope,
            CancellationToken cancellationToken)
        {
            if (pendingWriteStep == null)
            {
                throw new ArgumentNullException(nameof(pendingWriteStep));
            }

            if (pendingWriteStep.State != PendingWriteState.AwaitingVerification)
            {
                var failureMessage = "当前写步骤不处于待验证状态，无法自动补验证。";
                return AutoVerifyOutcome.CreateFailed(
                    failureMessage,
                    "当前写步骤状态异常，任务已中止。");
            }

            if (!pendingWriteStep.HasAutoVerifyPlan)
            {
                return AutoVerifyOutcome.CreateFailed(
                    pendingWriteStep.VerificationFailureReason,
                    "当前写步骤缺少可执行的验证输入，当前步骤待修复。");
            }

            var verifyTool = _toolRegistry.GetTool(pendingWriteStep.VerificationToolName);
            if (verifyTool == null)
            {
                var failureMessage = "系统未找到内部验证工具实现，当前步骤待修复。";
                return AutoVerifyOutcome.CreateFailed(
                    failureMessage,
                    "系统内部验证工具不可用，当前步骤待修复。");
            }

            var autoVerifyCall = new ToolCall
            {
                Id = pendingWriteStep.ToolCallId + "__auto_verify",
                Name = pendingWriteStep.VerificationToolName,
                Input = pendingWriteStep.VerificationInput,
                Description = pendingWriteStep.VerificationOperationDescription
            };

            ToolCallResult executionResult;
            try
            {
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeoutCts.CancelAfter(ToolExecutionTimeout);
                    using (var inputDocument = JsonDocument.Parse(pendingWriteStep.VerificationInput))
                    {
                        var toolTask = verifyTool.ExecuteAsync(
                            inputDocument.RootElement.Clone(),
                            undoScope,
                            timeoutCts.Token);
                        var completedTask = await Task.WhenAny(
                                toolTask,
                                Task.Delay(ToolExecutionTimeout, cancellationToken))
                            .ConfigureAwait(false);
                        executionResult = completedTask == toolTask
                            ? await toolTask.ConfigureAwait(false)
                            : ToolCallResult.Error(autoVerifyCall.Name, "工具执行超时。");
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                executionResult = ToolCallResult.Error(autoVerifyCall.Name, "工具执行超时。");
            }
            catch (Exception ex)
            {
                executionResult = ToolCallResult.Error(
                    autoVerifyCall.Name,
                    Truncate(ex.ToString(), ToolErrorMessageMaxLength));
            }

            var verificationFailureMessage = BuildVerificationFailureMessage(executionResult);
            if (executionResult.Success && TryGetVerificationAllPassed(executionResult.Output, out var allPassed) && allPassed)
            {
                return AutoVerifyOutcome.CreatePassed(
                    autoVerifyCall,
                    executionResult,
                    pendingWriteStep.VerificationOperationDescription);
            }

            return AutoVerifyOutcome.CreateFailed(
                verificationFailureMessage,
                "当前写步骤的验证未通过，当前步骤待修复。",
                autoVerifyCall,
                executionResult,
                pendingWriteStep.VerificationOperationDescription);
        }

        private async Task AppendAutoVerifyObservationAsync(
            string documentPath,
            IList<AgentMessage> messages,
            PendingWriteStep pendingWriteStep,
            AutoVerifyOutcome outcome,
            AutoVerifyObservationDisposition disposition,
            CancellationToken cancellationToken)
        {
            var observation = BuildAutoVerifyObservationMessage(
                pendingWriteStep,
                outcome,
                disposition);

            await AppendInternalObservationAsync(documentPath, messages, observation, cancellationToken)
                .ConfigureAwait(false);
        }

        private static string BuildAutoVerifyObservationMessage(
            PendingWriteStep pendingWriteStep,
            AutoVerifyOutcome outcome,
            AutoVerifyObservationDisposition disposition)
        {
            var autoVerifyCall = outcome == null ? null : outcome.ToolCall;
            var executionResult = outcome == null ? null : outcome.Result;
            var verificationMessage = outcome == null ? string.Empty : outcome.FailureMessage;
            var allPassed = executionResult != null
                && executionResult.Success
                && TryGetVerificationAllPassed(executionResult.Output, out var parsedAllPassed)
                && parsedAllPassed;
            var builder = new StringBuilder();
            builder.AppendLine("[SmartWord 自动验证结果]");
            var stepDescription = pendingWriteStep == null || string.IsNullOrWhiteSpace(pendingWriteStep.OperationDescription)
                ? "当前写步骤"
                : pendingWriteStep.OperationDescription.Trim();
            if (allPassed && disposition == AutoVerifyObservationDisposition.Committed)
            {
                builder.AppendLine($"当前写步骤“{stepDescription}”已自动验证通过且已提交。请继续执行后续 Todo，不要重复该步骤。");
                return builder.ToString();
            }

            builder.AppendLine("系统已在写操作后执行自动验证。这不是用户的新需求，而是当前写步骤的内部观察结果。");
            builder.AppendLine();
            builder.AppendLine("当前写步骤：");
            builder.AppendLine("- " + stepDescription);
            builder.AppendLine();
            builder.AppendLine("验证工具：");
            builder.AppendLine("- " + (autoVerifyCall == null || string.IsNullOrWhiteSpace(autoVerifyCall.Name)
                ? "未执行"
                : autoVerifyCall.Name.Trim()));
            builder.AppendLine();
            builder.AppendLine("验证状态：");
            if (disposition == AutoVerifyObservationDisposition.RolledBack)
            {
                builder.AppendLine("- 当前写步骤未通过验证，当前失败写步骤已回退，之前已验证通过的步骤保持不变。");
            }
            else if (executionResult == null)
            {
                builder.AppendLine("- 自动验证未能执行。");
            }
            else if (!executionResult.Success)
            {
                builder.AppendLine("- 验证工具执行失败。");
            }
            else
            {
                builder.AppendLine("- 验证工具已执行，但当前写步骤未通过验证。");
            }

            if (!string.IsNullOrWhiteSpace(verificationMessage))
            {
                builder.AppendLine();
                builder.AppendLine("验证结论：");
                builder.AppendLine(verificationMessage.Trim());
            }

            if (executionResult != null && !string.IsNullOrWhiteSpace(executionResult.Output))
            {
                builder.AppendLine();
                builder.AppendLine("验证输出：");
                builder.AppendLine(Truncate(executionResult.Output, 2000));
            }

            builder.AppendLine();
            builder.AppendLine("下一步要求：");
            builder.AppendLine("- 请基于上面的验证结论和验证输出修复当前步骤。");
            builder.AppendLine("- 修复时优先使用更小范围、更稳妥的写操作，不要重复已经完成的前序 Todo。");
            builder.AppendLine("- 修复后仍必须通过验证。");
            return builder.ToString();
        }

        private static string BuildPatchRangeAutoVerifyInput(JArray checks)
        {
            var codeBuilder = new StringBuilder();
            codeBuilder.AppendLine("var results = new List<object>();");
            codeBuilder.AppendLine("bool allPassed = true;");
            codeBuilder.AppendLine("dynamic paragraphs = ActiveDoc == null ? null : ActiveDoc.Paragraphs;");
            codeBuilder.AppendLine("int paragraphCount = paragraphs == null ? 0 : Convert.ToInt32(paragraphs.Count);");
            codeBuilder.AppendLine("bool ParagraphExists(int index) { return index >= 0 && index < paragraphCount; }");
            codeBuilder.AppendLine("string NormalizeText(string text) { return string.IsNullOrEmpty(text) ? string.Empty : text.Replace(\"\\r\", string.Empty).Replace(\"\\a\", string.Empty).Trim(); }");
            codeBuilder.AppendLine("string ReadParagraphText(int index) { if (!ParagraphExists(index)) { return string.Empty; } dynamic paragraph = paragraphs[index + 1]; dynamic range = paragraph == null ? null : paragraph.Range; return NormalizeText(range == null ? string.Empty : Convert.ToString(range.Text)); }");
            codeBuilder.AppendLine("string ReadParagraphStyle(int index) { if (!ParagraphExists(index)) { return string.Empty; } dynamic paragraph = paragraphs[index + 1]; dynamic style = null; try { style = paragraph == null ? null : paragraph.get_Style(); if (style == null) { return string.Empty; } try { return Convert.ToString(style.NameLocal); } catch { return Convert.ToString(style); } } catch { return string.Empty; } }");
            codeBuilder.AppendLine("void AddResult(string checkKey, bool passed, string actual, string expected, string hint) { results.Add(new { check_key = checkKey, passed = passed, actual = actual, expected = expected, hint = passed ? string.Empty : hint }); if (!passed) { allPassed = false; } }");

            for (var index = 0; index < checks.Count; index++)
            {
                if (!(checks[index] is JObject check))
                {
                    continue;
                }

                AppendPatchRangeCheckScript(codeBuilder, check, index);
            }

            codeBuilder.AppendLine("return new { all_passed = allPassed, results = results };");

            return new JObject
            {
                ["description"] = "验证当前 patch_range 写步骤是否生效。",
                ["code"] = codeBuilder.ToString()
            }.ToString(Formatting.None);
        }

        private static void AppendPatchRangeCheckScript(StringBuilder builder, JObject check, int index)
        {
            var type = (check.Value<string>("type") ?? string.Empty).Trim().ToLowerInvariant();
            var paragraphIndex = check.Value<int?>("paragraph_index") ?? -1;
            var expected = check.Value<string>("expected") ?? string.Empty;
            var shouldExist = check.Value<bool?>("should_exist") ?? true;
            var checkKey = type + "_" + index;

            switch (type)
            {
                case "text_contains":
                    builder.AppendLine("{");
                    builder.AppendLine("    var actual = ReadParagraphText(" + paragraphIndex + ");");
                    builder.AppendLine("    var exists = ParagraphExists(" + paragraphIndex + ");");
                    builder.AppendLine("    var expected = " + JsonConvert.ToString(expected) + ";");
                    builder.AppendLine("    var passed = exists && !string.IsNullOrEmpty(expected) && actual.IndexOf(expected, StringComparison.Ordinal) >= 0;");
                    builder.AppendLine("    AddResult(" + JsonConvert.ToString(checkKey) + ", passed, actual, expected, \"文本未包含预期内容，建议先回读目标段落，再检查是否写入到了错误位置。\");");
                    builder.AppendLine("}");
                    break;
                case "text_equals":
                    builder.AppendLine("{");
                    builder.AppendLine("    var actual = ReadParagraphText(" + paragraphIndex + ");");
                    builder.AppendLine("    var exists = ParagraphExists(" + paragraphIndex + ");");
                    builder.AppendLine("    var expected = " + JsonConvert.ToString(expected) + ";");
                    builder.AppendLine("    var passed = exists && string.Equals(actual, expected, StringComparison.Ordinal);");
                    builder.AppendLine("    AddResult(" + JsonConvert.ToString(checkKey) + ", passed, actual, expected, \"文本与预期不完全一致，建议检查是否残留了原有内容或换行。\");");
                    builder.AppendLine("}");
                    break;
                case "text_not_contains":
                    builder.AppendLine("{");
                    builder.AppendLine("    var actual = ReadParagraphText(" + paragraphIndex + ");");
                    builder.AppendLine("    var exists = ParagraphExists(" + paragraphIndex + ");");
                    builder.AppendLine("    var expected = " + JsonConvert.ToString(expected) + ";");
                    builder.AppendLine("    var passed = exists && (string.IsNullOrEmpty(expected) || actual.IndexOf(expected, StringComparison.Ordinal) < 0);");
                    builder.AppendLine("    AddResult(" + JsonConvert.ToString(checkKey) + ", passed, actual, expected, \"目标文本仍然存在，建议改用更精确的范围写入或补充删除操作。\");");
                    builder.AppendLine("}");
                    break;
                case "style_equals":
                    builder.AppendLine("{");
                    builder.AppendLine("    var actual = ReadParagraphStyle(" + paragraphIndex + ");");
                    builder.AppendLine("    var exists = ParagraphExists(" + paragraphIndex + ");");
                    builder.AppendLine("    var expected = " + JsonConvert.ToString(expected) + ";");
                    builder.AppendLine("    var passed = exists && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);");
                    builder.AppendLine("    AddResult(" + JsonConvert.ToString(checkKey) + ", passed, actual, expected, \"段落样式未达到预期，建议确认样式名称是否与 Word 中的本地样式名一致。\");");
                    builder.AppendLine("}");
                    break;
                case "paragraph_exists":
                    builder.AppendLine("{");
                    builder.AppendLine("    var exists = ParagraphExists(" + paragraphIndex + ");");
                    builder.AppendLine("    var actual = exists ? \"true\" : \"false\";");
                    builder.AppendLine("    var expected = " + JsonConvert.ToString(shouldExist ? "true" : "false") + ";");
                    builder.AppendLine("    var passed = exists == " + (shouldExist ? "true" : "false") + ";");
                    builder.AppendLine("    AddResult(" + JsonConvert.ToString(checkKey) + ", passed, actual, expected, " + JsonConvert.ToString(shouldExist ? "目标段落不存在，建议先确认段落索引是否仍然有效。" : "目标段落仍然存在，删除操作可能没有真正命中段落标记。") + ");");
                    builder.AppendLine("}");
                    break;
            }
        }

        private static string NormalizeAutoVerifyText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text
                .Replace("\r", string.Empty)
                .Replace("\a", string.Empty)
                .Trim();
        }

        private static AgentEvent CreateChangeEvent(
            AgentEventType eventType,
            PendingWriteStep pendingWriteStep,
            string message,
            string toolOutput = null)
        {
            if (pendingWriteStep == null)
            {
                throw new ArgumentNullException(nameof(pendingWriteStep));
            }

            return new AgentEvent
            {
                Type = eventType,
                ToolCallId = pendingWriteStep.ToolCallId,
                ToolName = pendingWriteStep.ToolName,
                ToolOutput = toolOutput ?? string.Empty,
                AffectedParagraphs = pendingWriteStep.AffectedParagraphs,
                OperationDescription = pendingWriteStep.OperationDescription,
                Message = message ?? string.Empty
            };
        }

        private static AgentEvent CreatePendingWriteTerminationEvent(PendingWriteStep pendingWriteStep)
        {
            if (pendingWriteStep == null)
            {
                throw new ArgumentNullException(nameof(pendingWriteStep));
            }

            if (pendingWriteStep.State == PendingWriteState.AwaitingVerification)
            {
                return CreatePendingWriteErrorEvent(
                    pendingWriteStep,
                    "写步骤已执行，但任务在验证步骤完成前结束，系统已停止任务并回滚未确认写入。");
            }

            return CreatePendingWriteErrorEvent(
                pendingWriteStep,
                "写步骤失败后未完成修复，系统已停止任务并回滚本次任务中的写入。");
        }

        private static AgentEvent CreatePendingWriteStateEvent(PendingWriteStep pendingWriteStep)
        {
            if (pendingWriteStep == null)
            {
                throw new ArgumentNullException(nameof(pendingWriteStep));
            }

            if (pendingWriteStep.State == PendingWriteState.AwaitingVerification)
            {
                return CreateChangeEvent(
                    AgentEventType.ChangeUnverified,
                    pendingWriteStep,
                    "写步骤已执行，但任务在验证步骤完成前结束，当前步骤未被确认。");
            }

            return CreateChangeEvent(
                AgentEventType.ChangeRepairRequired,
                pendingWriteStep,
                string.IsNullOrWhiteSpace(pendingWriteStep.LastFailureMessage)
                    ? "写步骤失败后仍待修复。"
                    : pendingWriteStep.LastFailureMessage);
        }

        private static AgentEvent CreatePendingWriteErrorEvent(PendingWriteStep pendingWriteStep, string message)
        {
            if (pendingWriteStep == null)
            {
                throw new ArgumentNullException(nameof(pendingWriteStep));
            }

            return new AgentEvent
            {
                Type = AgentEventType.Error,
                ToolCallId = pendingWriteStep.ToolCallId,
                ToolName = pendingWriteStep.ToolName,
                AffectedParagraphs = pendingWriteStep.AffectedParagraphs,
                OperationDescription = pendingWriteStep.OperationDescription,
                Message = message ?? string.Empty
            };
        }

        private static bool TryGetVerificationAllPassed(string output, out bool allPassed)
        {
            allPassed = false;
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            try
            {
                var payload = JObject.Parse(output);
                var allPassedToken = payload["all_passed"];
                if (allPassedToken == null || allPassedToken.Type == JTokenType.Null)
                {
                    return false;
                }

                allPassed = allPassedToken.Value<bool>();
                return true;
            }
            catch (JsonReaderException)
            {
                return false;
            }
        }

        private static string BuildVerificationFailureMessage(ToolCallResult verificationResult)
        {
            if (verificationResult == null)
            {
                return "写步骤已执行，但验证步骤未返回结果，当前步骤待修复。";
            }

            if (!verificationResult.Success)
            {
                return "写步骤已执行，但验证步骤执行失败，当前步骤待修复。";
            }

            if (!TryGetVerificationAllPassed(verificationResult.Output, out var allPassed))
            {
                return "写步骤已执行，但验证步骤返回结果无法判定，当前步骤待修复。";
            }

            return allPassed
                ? "已通过验证步骤确认改动生效。"
                : "写步骤已执行，但验证步骤未全部通过，当前步骤待修复。";
        }

        private static string BuildWriteRepairMessage(ToolCallResult executionResult)
        {
            if (executionResult == null || string.IsNullOrWhiteSpace(executionResult.Output))
            {
                return "写步骤执行失败，当前步骤待修复。";
            }

            var normalized = executionResult.Output.Replace("\r\n", "\n");
            var firstLine = normalized
                .Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return "写步骤执行失败，当前步骤待修复。";
            }

            return "写步骤执行失败，当前步骤待修复：" + firstLine;
        }

        private static string BuildOperationDescription(string toolName, JObject parsedInput)
        {
            if (parsedInput != null)
            {
                var description = parsedInput.Value<string>("description");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }

                var operation = parsedInput.Value<string>("operation");
                if (!string.IsNullOrWhiteSpace(operation))
                {
                    switch ((toolName ?? string.Empty).Trim().ToLowerInvariant())
                    {
                        case "patch_range":
                            return "准备执行范围写入：" + operation.Trim();
                        case "read_script":
                            return "准备执行脚本查询：" + operation.Trim();
                        case "verify_script":
                            return "准备验证改动结果：" + operation.Trim();
                        case "execute_script":
                            return "准备执行脚本写入：" + operation.Trim();
                    }
                }

                if (parsedInput.TryGetValue("operations", out var operationsToken)
                    && operationsToken is JArray operationsArray)
                {
                    return "准备执行范围写入，共 " + operationsArray.Count + " 个操作。";
                }
            }

            switch ((toolName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "patch_range":
                    return "准备执行文档范围写入。";
                case "read_script":
                    return "准备执行脚本查询。";
                case "verify_script":
                    return "准备验证本次改动结果。";
                case "execute_script":
                    return "准备执行脚本写入。";
                default:
                    return "准备执行工具：" + (toolName ?? string.Empty);
            }
        }

        private static void NormalizeToolCalls(
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

        private static AgentMessage CloneMessage(AgentMessage message)
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

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxLength) + "...";
        }

        private static int ResolveMaxIterations(AgentRunOptions options)
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

        private static AgentPermissionMode ResolvePermissionMode(AgentRunOptions options)
        {
            if (options != null && options.PermissionMode.HasValue)
            {
                return options.PermissionMode.Value;
            }

            return options != null && !options.RequireConfirmationForScripts
                ? AgentPermissionMode.AutoSafeWrites
                : AgentPermissionMode.ConfirmWrites;
        }

        private static int ResolveTotalSteps(AgentRunOptions options)
        {
            return options == null || options.ActivePlan == null || options.ActivePlan.TodoList == null
                ? 0
                : options.ActivePlan.TodoList.Count;
        }

        private static TaskRunCompletion ResolveTaskRunCompletion(
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

        private static TaskRunCompletion CreateTaskRunCompletion(
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

        private static string ToTaskHistoryMode(AgentMode mode)
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

        private static string ToTaskHistoryPermissionMode(AgentPermissionMode mode)
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

        private enum PendingWriteState
        {
            AwaitingVerification = 0,
            RepairRequired = 1
        }

        private enum AutoVerifyObservationDisposition
        {
            Committed = 0,
            RolledBack = 1
        }

        private sealed class AutoVerifyPlan
        {
            public string ToolName { get; private set; } = string.Empty;

            public string InputJson { get; private set; } = string.Empty;

            public string OperationDescription { get; private set; } = string.Empty;

            public string FailureReason { get; private set; } = string.Empty;

            public bool IsSupported => !string.IsNullOrWhiteSpace(InputJson);

            public static AutoVerifyPlan Supported(string toolName, string inputJson, string operationDescription)
            {
                return new AutoVerifyPlan
                {
                    ToolName = toolName ?? string.Empty,
                    InputJson = inputJson ?? string.Empty,
                    OperationDescription = operationDescription ?? string.Empty
                };
            }

            public static AutoVerifyPlan Unsupported(string failureReason)
            {
                return new AutoVerifyPlan
                {
                    FailureReason = failureReason ?? string.Empty
                };
            }
        }

        private sealed class AutoVerifyOutcome
        {
            public bool Passed { get; private set; }

            public string FailureMessage { get; private set; } = string.Empty;

            public string TerminationMessage { get; private set; } = string.Empty;

            public ToolCall ToolCall { get; private set; }

            public ToolCallResult Result { get; private set; }

            public string OperationDescription { get; private set; } = string.Empty;

            public static AutoVerifyOutcome CreatePassed(
                ToolCall toolCall,
                ToolCallResult result,
                string operationDescription)
            {
                return new AutoVerifyOutcome
                {
                    Passed = true,
                    ToolCall = toolCall,
                    Result = result,
                    OperationDescription = operationDescription ?? string.Empty
                };
            }

            public static AutoVerifyOutcome CreateFailed(
                string failureMessage,
                string terminationMessage,
                ToolCall toolCall = null,
                ToolCallResult result = null,
                string operationDescription = null)
            {
                return new AutoVerifyOutcome
                {
                    Passed = false,
                    FailureMessage = failureMessage ?? string.Empty,
                    TerminationMessage = terminationMessage ?? string.Empty,
                    ToolCall = toolCall,
                    Result = result,
                    OperationDescription = operationDescription ?? string.Empty
                };
            }
        }

        /// <summary>
        /// 记录当前写步骤的状态，确保编排层在验证通过前不会进入下一独立写步骤。
        /// </summary>
        private sealed class PendingWriteStep
        {
            public string ToolCallId { get; private set; }

            public string ToolName { get; private set; }

            public int[] AffectedParagraphs { get; private set; }

            public string OperationDescription { get; private set; }

            public PendingWriteState State { get; private set; }

            public int RepairAttempts { get; private set; }

            public string LastFailureMessage { get; private set; } = string.Empty;

            public string VerificationToolName { get; private set; } = string.Empty;

            public string VerificationInput { get; private set; } = string.Empty;

            public string VerificationOperationDescription { get; private set; } = string.Empty;

            public string VerificationFailureReason { get; private set; } = string.Empty;

            public bool HasAutoVerifyPlan => !string.IsNullOrWhiteSpace(VerificationToolName)
                && !string.IsNullOrWhiteSpace(VerificationInput);

            public static PendingWriteStep CreateAwaitingVerification(
                ToolCall toolCall,
                ToolCallResult result,
                AutoVerifyPlan autoVerifyPlan)
            {
                return new PendingWriteStep
                {
                    ToolCallId = toolCall == null ? string.Empty : toolCall.Id ?? string.Empty,
                    ToolName = toolCall == null ? string.Empty : toolCall.Name ?? string.Empty,
                    AffectedParagraphs = result?.AffectedParagraphs,
                    OperationDescription = result?.OperationDescription ?? string.Empty,
                    State = PendingWriteState.AwaitingVerification,
                    VerificationToolName = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.ToolName,
                    VerificationInput = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.InputJson,
                    VerificationOperationDescription = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.OperationDescription,
                    VerificationFailureReason = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.FailureReason
                };
            }

            public static PendingWriteStep CreateRepairRequired(
                ToolCall toolCall,
                ToolCallResult result,
                string operationDescription)
            {
                return new PendingWriteStep
                {
                    ToolCallId = toolCall == null ? string.Empty : toolCall.Id ?? string.Empty,
                    ToolName = toolCall == null ? string.Empty : toolCall.Name ?? string.Empty,
                    AffectedParagraphs = result?.AffectedParagraphs,
                    OperationDescription = string.IsNullOrWhiteSpace(result?.OperationDescription)
                        ? operationDescription ?? string.Empty
                        : result.OperationDescription,
                    State = PendingWriteState.RepairRequired,
                    RepairAttempts = 1,
                    LastFailureMessage = BuildWriteRepairMessage(result),
                    VerificationToolName = string.Empty,
                    VerificationInput = string.Empty,
                    VerificationOperationDescription = string.Empty,
                    VerificationFailureReason = string.Empty
                };
            }

            public PendingWriteStep MarkRepairRequired(string failureMessage)
            {
                return new PendingWriteStep
                {
                    ToolCallId = ToolCallId,
                    ToolName = ToolName,
                    AffectedParagraphs = AffectedParagraphs,
                    OperationDescription = OperationDescription,
                    State = PendingWriteState.RepairRequired,
                    RepairAttempts = RepairAttempts,
                    LastFailureMessage = failureMessage ?? string.Empty,
                    VerificationToolName = VerificationToolName,
                    VerificationInput = VerificationInput,
                    VerificationOperationDescription = VerificationOperationDescription,
                    VerificationFailureReason = VerificationFailureReason
                };
            }

            public PendingWriteStep MarkWriteExecuted(
                ToolCall toolCall,
                ToolCallResult result,
                AutoVerifyPlan autoVerifyPlan)
            {
                return new PendingWriteStep
                {
                    ToolCallId = toolCall == null ? ToolCallId : toolCall.Id ?? string.Empty,
                    ToolName = toolCall == null ? ToolName : toolCall.Name ?? string.Empty,
                    AffectedParagraphs = result?.AffectedParagraphs ?? AffectedParagraphs,
                    OperationDescription = string.IsNullOrWhiteSpace(result?.OperationDescription)
                        ? OperationDescription
                        : result.OperationDescription,
                    State = PendingWriteState.AwaitingVerification,
                    RepairAttempts = RepairAttempts,
                    LastFailureMessage = string.Empty,
                    VerificationToolName = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.ToolName,
                    VerificationInput = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.InputJson,
                    VerificationOperationDescription = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.OperationDescription,
                    VerificationFailureReason = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.FailureReason
                };
            }

            public PendingWriteStep RegisterWriteFailure(
                ToolCall toolCall,
                ToolCallResult result,
                string operationDescription)
            {
                return new PendingWriteStep
                {
                    ToolCallId = toolCall == null ? ToolCallId : toolCall.Id ?? string.Empty,
                    ToolName = toolCall == null ? ToolName : toolCall.Name ?? string.Empty,
                    AffectedParagraphs = result?.AffectedParagraphs ?? AffectedParagraphs,
                    OperationDescription = string.IsNullOrWhiteSpace(result?.OperationDescription)
                        ? (string.IsNullOrWhiteSpace(OperationDescription) ? operationDescription ?? string.Empty : OperationDescription)
                        : result.OperationDescription,
                    State = PendingWriteState.RepairRequired,
                    RepairAttempts = RepairAttempts + 1,
                    LastFailureMessage = BuildWriteRepairMessage(result),
                    VerificationToolName = VerificationToolName,
                    VerificationInput = VerificationInput,
                    VerificationOperationDescription = VerificationOperationDescription,
                    VerificationFailureReason = VerificationFailureReason
                };
            }
        }
    }
}
