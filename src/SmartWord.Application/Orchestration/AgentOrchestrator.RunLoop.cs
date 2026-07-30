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
using static SmartWord.Application.Orchestration.AgentEventFactory;
using static SmartWord.Application.Orchestration.AgentOrchestratorUtilities;
using static SmartWord.Application.Orchestration.WriteOperationState;

namespace SmartWord.Application.Orchestration
{
    public sealed partial class AgentOrchestrator
    {
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
            // 内部编排流负责 Undo、Todo 和审计清理；公开边界统一把用户取消转换为领域事件。
            var enumerator = RunCoreAsync(userInput, options, cancellationToken).GetAsyncEnumerator();
            try
            {
                while (true)
                {
                    bool moved;
                    var cancelled = false;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        moved = false;
                        cancelled = true;
                    }

                    if (cancelled)
                    {
                        yield return new AgentEvent
                        {
                            Type = AgentEventType.Cancelled,
                            Message = "任务已取消，当前未完成的写步骤已回滚。"
                        };
                        yield break;
                    }

                    if (!moved)
                    {
                        yield break;
                    }

                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async IAsyncEnumerable<AgentEvent> RunCoreAsync(
            string userInput,
            AgentRunOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
{
    var safeOptions = options ?? new AgentRunOptions();
    var documentContext = await _contextHydrator.HydrateAsync(cancellationToken).ConfigureAwait(false);
    var documentPath = string.IsNullOrWhiteSpace(documentContext.DocumentPath)
        ? "__active_document__"
        : documentContext.DocumentPath;
    var conversationStorageKey = ResolveConversationStorageKey(
        documentPath,
        safeOptions.ConversationId);
    var taskStartedAtUtc = DateTimeOffset.UtcNow;
    _todoRunCoordinator.SetCurrentDocumentPath(documentPath);
    var auditRun = await _runAuditRecorder.TryStartTaskRunAsync(
            documentPath,
            userInput,
            safeOptions,
            cancellationToken)
        .ConfigureAwait(false);
    await _runAuditRecorder.RecordTaskTelemetryAsync(
            "task_started",
            safeOptions,
            new Dictionary<string, object>
            {
                ["inputDocx"] = documentPath,
                ["startedAtUtc"] = taskStartedAtUtc.ToString("O"),
                ["status"] = "running"
            },
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
        await _runAuditRecorder.TryCompleteTaskRunAsync(auditRun, auditCompletion, CancellationToken.None)
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
        .AppendUserMessageAsync(conversationStorageKey, userMessage, cancellationToken)
        .ConfigureAwait(false);

    TodoBoard currentTodoBoard = null;
    var activePlanFingerprint = string.Empty;
    var runStarted = false;
    await foreach (var startupUpdate in _todoRunCoordinator
        .StartRunAsync(documentPath, safeOptions, cancellationToken)
        .ConfigureAwait(false))
    {
        currentTodoBoard = startupUpdate.Board ?? currentTodoBoard;
        if (!string.IsNullOrWhiteSpace(startupUpdate.ActivePlanFingerprint))
        {
            activePlanFingerprint = startupUpdate.ActivePlanFingerprint;
        }

        if (startupUpdate.Event != null)
        {
            yield return startupUpdate.Event;
        }

        runStarted = runStarted || startupUpdate.RunStarted;
        if (startupUpdate.ShouldStop)
        {
            yield break;
        }
    }

    var history = await _conversationStore
        .GetHistoryAsync(conversationStorageKey, cancellationToken)
        .ConfigureAwait(false);

    var messages = new List<AgentMessage>();
    var skillPromptContext = await ResolveSkillPromptContextAsync(
            userInput,
            safeOptions,
            cancellationToken)
        .ConfigureAwait(false);
    var systemPrompt = BuildSystemPrompt(safeOptions, documentContext, currentTodoBoard, skillPromptContext);
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
    var runState = new AgentRunState();
    var maxIterations = ResolveMaxIterations(safeOptions);
    var completedSuccessfully = false;
    var hasSuccessfulDocumentWriteOccurredInRun = false;
    PendingWriteStep pendingWriteStep = null;
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

            var compressionContext = CreateCompressionContext(
                safeOptions,
                documentPath,
                latestContext,
                currentTodoBoard,
                pendingWriteStep,
                messages);
            var compactionResult = await _contextCompactionService
                .CompactIfNeededAsync(messages, safeOptions, compressionContext, cancellationToken)
                .ConfigureAwait(false);
            if (compactionResult.WasCompacted)
            {
                var beforeTokens = _conversationStore.EstimateTokenCount(messages);
                var afterTokens = _conversationStore.EstimateTokenCount(compactionResult.Messages.ToList());
                await _runAuditRecorder.RecordTaskTelemetryAsync(
                        "context_compressed",
                        safeOptions,
                        new Dictionary<string, object>
                        {
                            ["beforeTokens"] = beforeTokens,
                            ["afterTokens"] = afterTokens,
                            ["tokensSaved"] = Math.Max(0, beforeTokens - afterTokens),
                            ["messageCountBefore"] = messages.Count,
                            ["messageCountAfter"] = compactionResult.Messages == null ? 0 : compactionResult.Messages.Count,
                            ["strategy"] = compactionResult.Message ?? string.Empty,
                            ["wasCompacted"] = true
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                yield return new AgentEvent
                {
                    Type = AgentEventType.ContextCompacted,
                    Message = compactionResult.Message
                };
            }

            if (compactionResult.ShouldStop)
            {
                if (safeOptions.Mode == AgentMode.Agent)
                {
                    interruptedOutcome = TodoBoardRunOutcome.Failed;
                    interruptedReason = compactionResult.Message;
                }

                yield return new AgentEvent
                {
                    Type = AgentEventType.Error,
                    Message = compactionResult.Message
                };
                yield break;
            }

            if (compactionResult.WasCompacted)
            {
                messages = compactionResult.Messages.ToList();
            }

            AgentMessage assistantMessage = null;
            var requireToolCall = toolDefinitions != null
                && toolDefinitions.Count > 0
                && RequiresFreshDocumentToolCall(userInput, safeOptions.Mode, iteration);
            await foreach (var update in _llmTurnExecutor.ExecuteAsync(
                messages,
                safeOptions,
                toolDefinitions,
                requireToolCall,
                cancellationToken))
            {
                if (update.IsFailed)
                {
                    interruptedOutcome = update.Error is OperationCanceledException
                        ? TodoBoardRunOutcome.Cancelled
                        : TodoBoardRunOutcome.Failed;
                    interruptedReason = update.Error is OperationCanceledException
                        ? "当前 Agent 运行已被取消。"
                        : string.IsNullOrWhiteSpace(update.Error.Message)
                            ? "当前 Agent 运行发生未预期异常。"
                            : update.Error.Message;
                    throw update.Error;
                }

                if (update.IsCompleted)
                {
                    assistantMessage = update.AssistantMessage;
                    continue;
                }

                yield return new AgentEvent
                {
                    Type = AgentEventType.StreamChunk,
                    Content = update.Chunk
                };
            }

            if (assistantMessage == null)
            {
                throw new InvalidOperationException("当前模型调用没有返回完整消息。");
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

            runState.FinalAssistantMessage = assistantMessage;
            await _conversationStore
                .AppendAssistantMessageAsync(conversationStorageKey, assistantMessage, cancellationToken)
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
                    var pendingWriteStateEvent = WriteStepCoordinator.CreatePendingWriteStateEvent(pendingWriteStep);
                    await _runAuditRecorder.TryRecordTaskChangeAsync(
                            auditRun?.Id,
                            pendingWriteStateEvent,
                            pendingWriteStateEvent.Type == AgentEventType.ChangeUnverified ? "unverified" : "repair_required",
                            cancellationToken)
                        .ConfigureAwait(false);
                    yield return pendingWriteStateEvent;
                    if (safeOptions.Mode == AgentMode.Agent && runStarted && _todoRunCoordinator.IsAvailable)
                    {
                        currentTodoBoard = await _todoRunCoordinator
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
                            _todoRunCoordinator.Manager,
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

                if (_planInterviewCoordinator.IsInterviewCall(toolCall, safeOptions.Mode))
                {
                    var interviewRequest = _planInterviewCoordinator.Prepare(toolCall, runState.InterviewRound);
                    if (!interviewRequest.IsValid)
                    {
                        var invalidQuestionResult = ToolCallResult.Error(
                            toolCall.Name,
                            "ask_user_question 缺少有效的 question 文本，系统已拒绝本次采访问题。");
                        await AppendToolResultAsync(conversationStorageKey, messages, toolCall, invalidQuestionResult, cancellationToken)
                            .ConfigureAwait(false);

                        yield return CreateToolCompletedEvent(toolCall, invalidQuestionResult);

                        RecordConsecutiveFailure(
                            ref runState.ConsecutiveFailures,
                            ref runState.LastFailureSummary,
                            toolCall.Name,
                            invalidQuestionResult.Output);
                        if (runState.ConsecutiveFailures >= ConsecutiveFailureThreshold)
                        {
                            interruptedOutcome = TodoBoardRunOutcome.Failed;
                            interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                            yield return CreateCircuitBreakerEvent(runState.LastFailureSummary);
                            yield break;
                        }

                        continue;
                    }

                    runState.InterviewRound = interviewRequest.InterviewRound;
                    Log.Information(
                        "Plan 模式发起采访问题。Iteration={Iteration}, InterviewRound={InterviewRound}, ToolCallId={ToolCallId}, Question={Question}",
                        iteration + 1,
                        runState.InterviewRound,
                        toolCall.Id,
                        interviewRequest.Question);

                    // 必须先发送问题事件，前端收到后才能通过通道提交回答。
                    yield return interviewRequest.QuestionEvent;

                    Log.Information(
                        "Plan 模式等待用户回答。ToolCallId={ToolCallId}",
                        toolCall.Id);
                    var answer = await _planInterviewCoordinator
                        .WaitForAnswerAsync(toolCall.Id, cancellationToken)
                        .ConfigureAwait(false);
                    Log.Information(
                        "Plan 模式已收到用户回答。ToolCallId={ToolCallId}, AnswerLength={AnswerLength}",
                        toolCall.Id,
                        answer == null ? 0 : answer.Length);

                    if (interviewRequest.ReachedLimit)
                    {
                        messages.Add(_planInterviewCoordinator.CreateRoundLimitMessage(answer));
                    }
                    else
                    {
                        await AppendToolResultAsync(conversationStorageKey, messages, toolCall,
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
                    await AppendToolResultAsync(conversationStorageKey, messages, toolCall, internalOnlyResult, cancellationToken)
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
                        ref runState.ConsecutiveFailures,
                        ref runState.LastFailureSummary,
                        toolCall.Name,
                        internalOnlyResult.Output);
                    if (runState.ConsecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                        yield return CreateCircuitBreakerEvent(runState.LastFailureSummary);
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

                // 只有真正修改 Word 文档的工具才进入写步骤修复与验证状态机。
                var isDocumentWriteTool = IsDocumentWriteTool(toolCall.Name);

                if (pendingWriteStep != null && !isDocumentWriteTool && !IsRepairProbeTool(toolCall.Name))
                {
                    var repairOnlyResult = ToolCallResult.Denied(
                        toolCall.Name,
                        "当前仍有待修复的写步骤。此时仅允许使用 read_script 做只读探针，或直接使用 patch_range / execute_script 修复当前失败步骤。");
                    await AppendToolResultAsync(conversationStorageKey, messages, toolCall, repairOnlyResult, cancellationToken)
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
                        ref runState.ConsecutiveFailures,
                        ref runState.LastFailureSummary,
                        toolCall.Name,
                        repairOnlyResult.Output);
                    if (runState.ConsecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                        yield return CreateCircuitBreakerEvent(runState.LastFailureSummary);
                        yield break;
                    }

                    shouldContinueWithNextAssistantTurn = true;
                    remainingToolCallsStartIndex = toolCallIndex + 1;
                    remainingToolCallsReason = "当前仍有待修复的写步骤，剩余工具调用已跳过。";
                    break;
                }

                var preparation = await _toolCallCoordinator
                    .PrepareAsync(toolCall, safeOptions, cancellationToken)
                    .ConfigureAwait(false);
                var tool = preparation.Tool;
                var parsedInput = preparation.ParsedInput;
                var inputParseError = preparation.InputParseError;
                var operationDescription = preparation.OperationDescription;
                var eventToolInput = preparation.EventToolInput;
                var permissionDecision = preparation.PermissionDecision;
                var requiresConfirmation = preparation.RequiresConfirmation;
                var scriptApprovalKey = preparation.ScriptApprovalKey;

                yield return new AgentEvent
                {
                    Type = AgentEventType.ToolCallStarted,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.Name,
                    ToolInput = eventToolInput,
                    RequiresConfirmation = requiresConfirmation,
                    OperationDescription = operationDescription
                };
                await _runAuditRecorder.RecordTaskTelemetryAsync(
                        "tool_call_started",
                        safeOptions,
                        new Dictionary<string, object>
                        {
                            ["toolCallId"] = toolCall.Id ?? string.Empty,
                            ["toolName"] = toolCall.Name ?? string.Empty,
                            ["rawInput"] = toolCall.Input ?? string.Empty,
                            ["operationDescription"] = operationDescription,
                            ["startedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["requiresConfirmation"] = requiresConfirmation
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                await _runAuditRecorder.RecordTaskTelemetryAsync(
                        "permission_checked",
                        safeOptions,
                        new Dictionary<string, object>
                        {
                            ["toolCallId"] = toolCall.Id ?? string.Empty,
                            ["toolName"] = toolCall.Name ?? string.Empty,
                            ["mode"] = safeOptions.Mode.ToString(),
                            ["permissionMode"] = ResolvePermissionMode(safeOptions).ToString(),
                            ["decision"] = permissionDecision.IsAllowed ? "allow" : "deny",
                            ["reason"] = permissionDecision.Reason ?? string.Empty,
                            ["requiresConfirmation"] = requiresConfirmation
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!permissionDecision.IsAllowed)
                {
                    var deniedResult = ToolCallResult.Denied(toolCall.Name, permissionDecision.Reason);
                    await _runAuditRecorder.RecordToolTelemetryAsync(
                            "tool_call_denied",
                            safeOptions,
                            toolCall,
                            deniedResult,
                            operationDescription,
                            requiresConfirmation,
                            false,
                            0,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await AppendToolResultAsync(conversationStorageKey, messages, toolCall, deniedResult, cancellationToken, auditRun?.Id, operationDescription)
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
                        ref runState.ConsecutiveFailures,
                        ref runState.LastFailureSummary,
                        toolCall.Name,
                        deniedResult.Output);
                    if (runState.ConsecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                        yield return CreateCircuitBreakerEvent(runState.LastFailureSummary);
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
                    await _runAuditRecorder.RecordToolTelemetryAsync(
                            "tool_call_failed",
                            safeOptions,
                            toolCall,
                            inputParseError,
                            operationDescription,
                            requiresConfirmation,
                            false,
                            0,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await AppendToolResultAsync(conversationStorageKey, messages, toolCall, inputParseError, cancellationToken, auditRun?.Id, operationDescription)
                        .ConfigureAwait(false);

                    yield return CreateToolCompletedEvent(toolCall, inputParseError);

                    RecordConsecutiveFailure(
                        ref runState.ConsecutiveFailures,
                        ref runState.LastFailureSummary,
                        toolCall.Name,
                        inputParseError.Output);
                    if (runState.ConsecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                        yield return CreateCircuitBreakerEvent(runState.LastFailureSummary);
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
                        await AppendToolResultAsync(conversationStorageKey, messages, toolCall, unavailableResult, cancellationToken, auditRun?.Id, operationDescription)
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
                            ref runState.ConsecutiveFailures,
                            ref runState.LastFailureSummary,
                            toolCall.Name,
                            unavailableResult.Output);
                        if (runState.ConsecutiveFailures >= ConsecutiveFailureThreshold)
                        {
                            interruptedOutcome = TodoBoardRunOutcome.Failed;
                            interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                            yield return CreateCircuitBreakerEvent(runState.LastFailureSummary);
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

                    var confirmationStartedAt = DateTimeOffset.UtcNow;
                    await _runAuditRecorder.RecordTaskTelemetryAsync(
                            "confirmation_requested",
                            safeOptions,
                            new Dictionary<string, object>
                            {
                                ["toolCallId"] = toolCall.Id ?? string.Empty,
                                ["toolName"] = toolCall.Name ?? string.Empty,
                                ["requestedAtUtc"] = confirmationStartedAt.ToString("O"),
                                ["policy"] = "runtime_channel"
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    var confirmationDecision = await WaitForToolConfirmationDecisionAsync(
                            toolCall,
                            eventToolInput,
                            operationDescription,
                            scriptApprovalKey,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await _runAuditRecorder.RecordTaskTelemetryAsync(
                            "confirmation_decided",
                            safeOptions,
                            new Dictionary<string, object>
                            {
                                ["toolCallId"] = toolCall.Id ?? string.Empty,
                                ["toolName"] = toolCall.Name ?? string.Empty,
                                ["requestedAtUtc"] = confirmationStartedAt.ToString("O"),
                                ["decidedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                                ["durationMs"] = (long)(DateTimeOffset.UtcNow - confirmationStartedAt).TotalMilliseconds,
                                ["confirmed"] = confirmationDecision != null && confirmationDecision.Confirmed,
                                ["remember"] = confirmationDecision != null && confirmationDecision.Remember,
                                ["policy"] = "runtime_channel",
                                ["reason"] = confirmationDecision == null ? "未收到确认决策。" : string.Empty
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (confirmationDecision == null || !confirmationDecision.Confirmed)
                    {
                        var skippedResult = ToolCallResult.Skipped(toolCall.Name, "用户选择跳过本次写操作。");
                        await _runAuditRecorder.RecordToolTelemetryAsync(
                                "tool_call_skipped",
                                safeOptions,
                                toolCall,
                                skippedResult,
                                operationDescription,
                                requiresConfirmation,
                                false,
                                0,
                                cancellationToken)
                            .ConfigureAwait(false);
                        await AppendToolResultAsync(conversationStorageKey, messages, toolCall, skippedResult, cancellationToken, auditRun?.Id, operationDescription)
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

                        runState.ConsecutiveFailures = 0;
                        if (pendingWriteStep != null)
                        {
                            shouldContinueWithNextAssistantTurn = true;
                            remainingToolCallsStartIndex = toolCallIndex + 1;
                            remainingToolCallsReason = "当前轮次已停在待修复状态，剩余工具调用已跳过。";
                            break;
                        }

                        continue;
                    }

                    if (confirmationDecision.Remember && scriptApprovalKey != null)
                    {
                        await _toolCallCoordinator
                            .RememberApprovalAsync(scriptApprovalKey, operationDescription, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                ToolCallResult executionResult;
                var toolExecutionStartedAtUtc = DateTimeOffset.UtcNow;
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
                            if (_todoRunCoordinator.IsAvailable)
                            {
                                currentTodoBoard = await _todoRunCoordinator
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
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    throw new OperationCanceledException(cancellationToken);
                                }

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

                    if (todoWriteStepStarted && _todoRunCoordinator.IsAvailable)
                    {
                        currentTodoBoard = await _todoRunCoordinator
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

                await _runAuditRecorder.RecordToolTelemetryAsync(
                        executionResult.Success ? "tool_call_completed" : "tool_call_failed",
                        safeOptions,
                        toolCall,
                        executionResult,
                        operationDescription,
                        requiresConfirmation,
                        requiresConfirmation,
                        (long)(DateTimeOffset.UtcNow - toolExecutionStartedAtUtc).TotalMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (executionResult.Success)
                {
                    executionResult.Output = DecorateToolOutput(
                        toolCall.Name,
                        executionResult.Output,
                        documentPath,
                        runState.CitationRegistry,
                        runState.ParagraphToRef,
                        ref runState.NextCitationRef);
                }

                if (string.IsNullOrWhiteSpace(executionResult.OperationDescription))
                {
                    executionResult.OperationDescription = operationDescription;
                }

                await AppendToolResultAsync(conversationStorageKey, messages, toolCall, executionResult, cancellationToken, auditRun?.Id, operationDescription)
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
                        runState.ConsecutiveFailures = 0;
                        var autoVerifyPlan = _writeStepCoordinator.BuildAutoVerifyPlan(toolCall.Name, parsedInput);
                        var executedWriteStep = pendingWriteStep != null
                            ? pendingWriteStep.MarkWriteExecuted(toolCall, executionResult, autoVerifyPlan)
                            : PendingWriteStep.CreateAwaitingVerification(toolCall, executionResult, autoVerifyPlan);
                        var changeExecutedEvent = WriteStepCoordinator.CreateChangeEvent(
                            AgentEventType.ChangeExecuted,
                            executedWriteStep,
                            "写入已执行，系统正在执行验证步骤。");
                        await _runAuditRecorder.TryRecordTaskChangeAsync(auditRun?.Id, changeExecutedEvent, "executed", cancellationToken)
                            .ConfigureAwait(false);
                        yield return changeExecutedEvent;

                        var autoVerifyOutcome = await _writeStepCoordinator.ExecuteAutoVerifyAsync(
                                executedWriteStep,
                                writeStepUndoScope,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (autoVerifyOutcome.ToolCall != null)
                        {
                            yield return CreateToolStartedEvent(
                                autoVerifyOutcome.ToolCall,
                                autoVerifyOutcome.OperationDescription);
                            await _runAuditRecorder.RecordVerificationTelemetryAsync(
                                    autoVerifyOutcome.Passed ? "verification_completed" : "verification_failed",
                                    safeOptions,
                                    autoVerifyOutcome.ToolCall,
                                    autoVerifyOutcome.Result,
                                    autoVerifyOutcome.OperationDescription,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            await _runAuditRecorder.TryRecordTaskToolAsync(
                                    auditRun?.Id,
                                    autoVerifyOutcome.ToolCall,
                                    autoVerifyOutcome.Result,
                                    autoVerifyOutcome.OperationDescription,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            yield return CreateToolCompletedEvent(autoVerifyOutcome.ToolCall, autoVerifyOutcome.Result);
                        }

                        var writeStepTransition = _writeStepCoordinator.ApplyVerificationOutcome(
                            executedWriteStep,
                            autoVerifyOutcome,
                            writeStepUndoScope);
                        writeStepCommitted = writeStepTransition.UndoCommitted;
                        writeStepRolledBack = writeStepTransition.UndoRolledBack;
                        pendingWriteStep = writeStepTransition.PendingWriteStep;

                        if (!writeStepTransition.Passed)
                        {
                            if (todoWriteStepStarted && _todoRunCoordinator.IsAvailable)
                            {
                                currentTodoBoard = await _todoRunCoordinator
                                    .RollbackCurrentWriteStepAsync(
                                        documentPath,
                                        pendingWriteStep.LastFailureMessage,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                yield return CreateTodoBoardSnapshotEvent(
                                    AgentEventType.TodoBoardUpdated,
                                    currentTodoBoard,
                                    _todoRunCoordinator.Manager,
                                    TodoBoardUpdateKind.RollbackRestored,
                                    "当前写步骤已回退，Todo Board 已恢复到最近可信检查点。");
                            }

                            await _writeStepCoordinator.AppendAutoVerifyObservationAsync(
                                conversationStorageKey,
                                messages,
                                executedWriteStep,
                                autoVerifyOutcome,
                                AutoVerifyObservationDisposition.RolledBack,
                                cancellationToken)
                                .ConfigureAwait(false);

                            var verificationFailedEvent = WriteStepCoordinator.CreateChangeEvent(
                                AgentEventType.ChangeVerificationFailed,
                                pendingWriteStep,
                                pendingWriteStep.LastFailureMessage,
                                autoVerifyOutcome.Result == null ? string.Empty : autoVerifyOutcome.Result.Output);
                            await _runAuditRecorder.TryRecordTaskChangeAsync(auditRun?.Id, verificationFailedEvent, "verification_failed", cancellationToken)
                                .ConfigureAwait(false);
                            yield return verificationFailedEvent;

                            if (pendingWriteStep.RepairAttempts >= WriteRepairAttemptLimit)
                            {
                                var pauseMessage = "当前写步骤已连续失败 3 次，系统已回退当前步骤并暂停。你可以继续尝试、跳过此步骤，或停止本次任务。";
                                if (safeOptions.Mode == AgentMode.Agent && runStarted && _todoRunCoordinator.IsAvailable)
                                {
                                    currentTodoBoard = await _todoRunCoordinator
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
                                        _todoRunCoordinator.Manager,
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
                            if (todoWriteStepStarted && _todoRunCoordinator.IsAvailable)
                            {
                                currentTodoBoard = await _todoRunCoordinator
                                    .MarkWriteStepCommittedAsync(
                                        documentPath,
                                        executedWriteStep.OperationDescription,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            await _writeStepCoordinator.AppendAutoVerifyObservationAsync(
                                conversationStorageKey,
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

                            var changeAppliedEvent = WriteStepCoordinator.CreateChangeEvent(
                                AgentEventType.ChangeApplied,
                                executedWriteStep,
                                "已通过验证步骤确认改动生效。",
                                autoVerifyOutcome.Result == null ? string.Empty : autoVerifyOutcome.Result.Output);
                            completedStepsForAudit++;
                            await _runAuditRecorder.TryRecordTaskChangeAsync(auditRun?.Id, changeAppliedEvent, "verified", cancellationToken)
                                .ConfigureAwait(false);
                            yield return changeAppliedEvent;
                            runState.ConsecutiveFailures = 0;
                        }

                        shouldContinueWithNextAssistantTurn = true;
                        remainingToolCallsStartIndex = toolCallIndex + 1;
                        remainingToolCallsReason = "当前轮次已进入写后自动验证，剩余工具调用已跳过。";
                        break;
                    }

                    runState.ConsecutiveFailures = 0;
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

                        if (todoWriteStepStarted && _todoRunCoordinator.IsAvailable)
                        {
                            currentTodoBoard = await _todoRunCoordinator
                                .RollbackCurrentWriteStepAsync(
                                    documentPath,
                                    pendingWriteStep.LastFailureMessage,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            yield return CreateTodoBoardSnapshotEvent(
                                AgentEventType.TodoBoardUpdated,
                                currentTodoBoard,
                                _todoRunCoordinator.Manager,
                                TodoBoardUpdateKind.RollbackRestored,
                                "当前写步骤已回退，Todo Board 已恢复到最近可信检查点。");
                        }

                        var repairRequiredEvent = WriteStepCoordinator.CreateChangeEvent(
                            AgentEventType.ChangeRepairRequired,
                            pendingWriteStep,
                            pendingWriteStep.LastFailureMessage,
                            executionResult.Output);
                        await _runAuditRecorder.TryRecordTaskChangeAsync(auditRun?.Id, repairRequiredEvent, "repair_required", cancellationToken)
                            .ConfigureAwait(false);
                        yield return repairRequiredEvent;

                        if (pendingWriteStep.RepairAttempts >= WriteRepairAttemptLimit)
                        {
                            var pauseMessage = "当前写步骤已连续失败 3 次，系统已回退当前步骤并暂停。你可以继续尝试、跳过此步骤，或停止本次任务。";
                            if (safeOptions.Mode == AgentMode.Agent && runStarted && _todoRunCoordinator.IsAvailable)
                            {
                                currentTodoBoard = await _todoRunCoordinator
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
                                    _todoRunCoordinator.Manager,
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
                        ref runState.ConsecutiveFailures,
                        ref runState.LastFailureSummary,
                        toolCall.Name,
                        executionResult.Output);
                    if (runState.ConsecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        interruptedOutcome = TodoBoardRunOutcome.Failed;
                        interruptedReason = "连续多次工具调用失败，系统已触发熔断停止。";
                        yield return CreateCircuitBreakerEvent(runState.LastFailureSummary);
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
                        if (todoWriteStepStarted && _todoRunCoordinator.IsAvailable)
                        {
                            currentTodoBoard = await _todoRunCoordinator
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

            if (_todoRunCoordinator.IsAvailable && safeOptions.Mode == AgentMode.Agent && currentTodoBoard != null)
            {
                if (todoWriteThisIteration)
                {
                    currentTodoBoard = await _todoRunCoordinator
                        .GetBoardAsync(documentPath, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (toolCalls.Count > 0)
                {
                    currentTodoBoard = await _todoRunCoordinator
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
                        conversationStorageKey,
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

                    currentTodoBoard = await _todoRunCoordinator
                        .MarkReminderInjectedAsync(
                            documentPath,
                            reminderDecision.IsHighPriority,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var reminderStats = _todoRunCoordinator.BuildStats(currentTodoBoard);
                    yield return new AgentEvent
                    {
                        Type = AgentEventType.TodoReminderInjected,
                        Message = reminderDecision.Message,
                        BoardJson = _todoRunCoordinator.SerializeBoard(currentTodoBoard),
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

                currentTodoBoard = await _todoRunCoordinator
                    .MarkReminderInjectedAsync(
                        documentPath,
                        reminderDecision.IsHighPriority,
                        cancellationToken)
                    .ConfigureAwait(false);

                var reminderStats = _todoRunCoordinator.BuildStats(currentTodoBoard);
                yield return new AgentEvent
                {
                    Type = AgentEventType.TodoReminderInjected,
                    Message = reminderDecision.Message,
                    BoardJson = _todoRunCoordinator.SerializeBoard(currentTodoBoard),
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
            var pendingWriteStateEvent = WriteStepCoordinator.CreatePendingWriteStateEvent(pendingWriteStep);
            await _runAuditRecorder.TryRecordTaskChangeAsync(
                    auditRun?.Id,
                    pendingWriteStateEvent,
                    pendingWriteStateEvent.Type == AgentEventType.ChangeUnverified ? "unverified" : "repair_required",
                    cancellationToken)
                .ConfigureAwait(false);
            yield return pendingWriteStateEvent;
            if (safeOptions.Mode == AgentMode.Agent && runStarted && _todoRunCoordinator.IsAvailable)
            {
                currentTodoBoard = await _todoRunCoordinator
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
                    _todoRunCoordinator.Manager,
                    pauseMessage);
            }

            yield break;
        }

        if (iteration >= maxIterations)
        {
            var maxIterationsMessage = BuildMaxIterationsMessage(safeOptions.Mode, maxIterations);
            yield return CreateMaxIterationsReachedEvent(safeOptions.Mode, maxIterations, maxIterationsMessage);

            if (safeOptions.Mode == AgentMode.Agent && runStarted && _todoRunCoordinator.IsAvailable)
            {
                currentTodoBoard = await _todoRunCoordinator
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
                    _todoRunCoordinator.Manager,
                    maxIterationsMessage);
            }

            yield break;
        }

        completedSuccessfully = true;
    }
    finally
    {
        if (!completedSuccessfully
            && !runPaused
            && cancellationToken.IsCancellationRequested)
        {
            interruptedOutcome = TodoBoardRunOutcome.Cancelled;
            if (string.IsNullOrWhiteSpace(interruptedReason))
            {
                interruptedReason = "用户已取消当前任务。";
            }
        }

        if (runStarted
            && !completedSuccessfully
            && !runPaused
            && interruptedOutcome != TodoBoardRunOutcome.None
            && _todoRunCoordinator.IsAvailable
            && safeOptions.Mode == AgentMode.Agent)
        {
            await _todoRunCoordinator
                .CompleteAsync(
                    documentPath,
                    runStarted,
                    false,
                    runPaused,
                    interruptedOutcome,
                    interruptedReason)
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

            await _runAuditRecorder.TryCompleteTaskRunAsync(auditRun, auditCompletion, CancellationToken.None)
                .ConfigureAwait(false);
            auditRunCompleted = true;
        }

        await _runAuditRecorder.RecordTaskTelemetryAsync(
                completedSuccessfully
                    ? "task_completed"
                    : interruptedOutcome == TodoBoardRunOutcome.Cancelled
                        ? "task_cancelled"
                        : "task_failed",
                safeOptions,
                new Dictionary<string, object>
                {
                    ["inputDocx"] = documentPath,
                    ["outputDocx"] = documentPath,
                    ["startedAtUtc"] = taskStartedAtUtc.ToString("O"),
                    ["durationMs"] = (long)(DateTimeOffset.UtcNow - taskStartedAtUtc).TotalMilliseconds,
                    ["status"] = completedSuccessfully
                        ? "completed"
                        : runPaused
                            ? "paused"
                            : interruptedOutcome == TodoBoardRunOutcome.Cancelled
                                ? "cancelled"
                                : "failed",
                    ["failureType"] = completedSuccessfully ? string.Empty : RunAuditRecorder.ResolveFailureType(interruptedOutcome),
                    ["failureReason"] = completedSuccessfully ? string.Empty : interruptedReason ?? string.Empty
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    if (runStarted && completedSuccessfully && _todoRunCoordinator.IsAvailable && safeOptions.Mode == AgentMode.Agent)
    {
        await _todoRunCoordinator
                .CompleteAsync(
                    documentPath,
                    runStarted,
                    true,
                    false,
                TodoBoardRunOutcome.None,
                string.Empty)
            .ConfigureAwait(false);
    }

    if (!completedSuccessfully)
    {
        yield break;
    }

    yield return new AgentEvent
    {
        Type = AgentEventType.TaskCompleted,
        Citations = BuildCitations(runState.FinalAssistantMessage?.Content, runState.CitationRegistry),
        Message = string.Empty
    };
}

    }
}

