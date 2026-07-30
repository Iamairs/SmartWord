using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Application.Todo;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using static SmartWord.Application.Orchestration.AgentEventFactory;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// 负责 Todo Board 的运行启动、恢复决策和最终收尾。
    /// 启动过程使用异步事件流，确保恢复事件先发送给前端，再等待用户决策。
    /// </summary>
    internal sealed class TodoRunCoordinator
    {
        private readonly TodoManager _todoManager;
        private readonly ITodoRecoveryChannel _todoRecoveryChannel;

        internal TodoRunCoordinator(
            TodoManager todoManager,
            ITodoRecoveryChannel todoRecoveryChannel)
        {
            _todoManager = todoManager;
            _todoRecoveryChannel = todoRecoveryChannel;
        }

        internal bool IsEnabled(AgentMode mode)
        {
            return _todoManager != null && mode == AgentMode.Agent;
        }

        internal bool IsAvailable
        {
            get { return _todoManager != null; }
        }

        // 事件工厂仍以 TodoManager 生成兼容的 Board JSON；生命周期调用全部留在本协调器内。
        internal TodoManager Manager
        {
            get { return _todoManager; }
        }

        internal void SetCurrentDocumentPath(string documentPath)
        {
            _todoManager?.SetCurrentDocumentPath(documentPath);
        }

        internal Task<TodoBoard> MarkRunPausedAsync(
            string documentPath,
            TodoBoardRunOutcome outcome,
            string reason,
            CancellationToken cancellationToken)
        {
            return _todoManager.MarkRunPausedAsync(documentPath, outcome, reason, cancellationToken);
        }

        internal Task<TodoBoard> MarkRunPausedAsync(
            string documentPath,
            string reason,
            CancellationToken cancellationToken)
        {
            return _todoManager.MarkRunPausedAsync(documentPath, reason, cancellationToken);
        }

        internal Task<TodoBoard> MarkWriteStepStartedAsync(
            string documentPath,
            string toolCallId,
            string operationDescription,
            CancellationToken cancellationToken)
        {
            return _todoManager.MarkWriteStepStartedAsync(documentPath, toolCallId, operationDescription, cancellationToken);
        }

        internal Task<TodoBoard> RollbackCurrentWriteStepAsync(
            string documentPath,
            string reason,
            CancellationToken cancellationToken)
        {
            return _todoManager.RollbackCurrentWriteStepAsync(documentPath, reason, cancellationToken);
        }

        internal Task<TodoBoard> MarkWriteStepCommittedAsync(
            string documentPath,
            string operationDescription,
            CancellationToken cancellationToken)
        {
            return _todoManager.MarkWriteStepCommittedAsync(documentPath, operationDescription, cancellationToken);
        }

        internal Task<TodoBoard> GetBoardAsync(
            string documentPath,
            CancellationToken cancellationToken)
        {
            return _todoManager.GetBoardAsync(documentPath, cancellationToken);
        }

        internal Task<TodoBoard> RecordRoundWithoutTodoWriteAsync(
            string documentPath,
            bool hasEffectiveExecutionRound,
            bool successfulDocumentWrite,
            CancellationToken cancellationToken)
        {
            return _todoManager.RecordRoundWithoutTodoWriteAsync(
                documentPath,
                hasEffectiveExecutionRound,
                successfulDocumentWrite,
                cancellationToken);
        }

        internal Task<TodoBoard> MarkReminderInjectedAsync(
            string documentPath,
            bool isHighPriority,
            CancellationToken cancellationToken)
        {
            return _todoManager.MarkReminderInjectedAsync(documentPath, isHighPriority, cancellationToken);
        }

        internal TodoBoardStats BuildStats(TodoBoard board)
        {
            return _todoManager.BuildStats(board);
        }

        internal string SerializeBoard(TodoBoard board)
        {
            return _todoManager.SerializeBoard(board);
        }

        internal string BuildPromptBlock(TodoBoard board)
        {
            return _todoManager.BuildPromptBlock(board);
        }

        internal async IAsyncEnumerable<TodoRunStartupUpdate> StartRunAsync(
            string documentPath,
            AgentRunOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var safeOptions = options ?? new AgentRunOptions();
            if (!IsEnabled(safeOptions.Mode))
            {
                yield break;
            }

            var activePlanFingerprint = _todoManager.ComputePlanFingerprint(safeOptions.ActivePlan);
            var prepareResult = await _todoManager
                .PrepareBoardForRunAsync(
                    documentPath,
                    safeOptions.ActivePlan,
                    forceRebuildFromActivePlan: safeOptions.ActivePlan != null
                        && !safeOptions.StartupTodoBoardDecision.HasValue,
                    cancellationToken)
                .ConfigureAwait(false);
            var currentTodoBoard = prepareResult.Board;
            activePlanFingerprint = string.IsNullOrWhiteSpace(prepareResult.ActivePlanFingerprint)
                ? activePlanFingerprint
                : prepareResult.ActivePlanFingerprint;

            if (RequiresRecoveryDecision(prepareResult))
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
                    yield return TodoRunStartupUpdate.Stop(
                        prepareResult.Board,
                        activePlanFingerprint,
                        new AgentEvent
                        {
                            Type = AgentEventType.Error,
                            Message = prepareResult.Status == TodoBoardPreparationStatus.Paused
                                ? "检测到已暂停的 Todo Board，但当前前端未连接继续决策通道，系统已停止执行。"
                                : "检测到待恢复的 Todo Board，但当前前端未连接恢复决策通道，系统已停止执行。"
                        });
                    yield break;
                }
                else
                {
                    var recoveryRequestId = Guid.NewGuid().ToString("N");
                    var recoveryEvent = prepareResult.Status == TodoBoardPreparationStatus.Paused
                        ? CreateTodoBoardPausedEvent(prepareResult, _todoManager, recoveryRequestId)
                        : CreateTodoBoardRecoveryRequiredEvent(prepareResult, _todoManager, recoveryRequestId);
                    yield return TodoRunStartupUpdate.PendingDecision(
                        prepareResult.Board,
                        activePlanFingerprint,
                        recoveryEvent);

                    var recoveryDecision = await _todoRecoveryChannel
                        .WaitForDecisionAsync(recoveryRequestId, cancellationToken)
                        .ConfigureAwait(false);
                    currentTodoBoard = await _todoManager
                        .ResolveRecoveryAsync(
                            documentPath,
                            recoveryDecision,
                            safeOptions.ActivePlan,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            currentTodoBoard = await _todoManager
                .MarkRunStartedAsync(
                    documentPath,
                    Guid.NewGuid().ToString("N"),
                    activePlanFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);

            var readyMessage = prepareResult.Status == TodoBoardPreparationStatus.RecoveryRequired
                ? "Todo Board 已按恢复决策准备完毕。"
                : prepareResult.Status == TodoBoardPreparationStatus.Paused
                    ? "Todo Board 已按继续决策准备完毕。"
                    : "当前 Todo Board 已就绪。";
            yield return TodoRunStartupUpdate.Started(
                currentTodoBoard,
                activePlanFingerprint,
                CreateTodoBoardReadyEvent(currentTodoBoard, _todoManager, readyMessage));
        }

        internal async System.Threading.Tasks.Task CompleteAsync(
            string documentPath,
            bool runStarted,
            bool completedSuccessfully,
            bool runPaused,
            TodoBoardRunOutcome interruptedOutcome,
            string interruptedReason)
        {
            if (_todoManager == null || !runStarted)
            {
                return;
            }

            if (completedSuccessfully)
            {
                await _todoManager
                    .MarkRunSucceededAndDeleteAsync(documentPath, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            if (!runPaused && interruptedOutcome != TodoBoardRunOutcome.None)
            {
                await _todoManager
                    .MarkRunInterruptedAsync(
                        documentPath,
                        interruptedOutcome,
                        interruptedReason,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        private static bool RequiresRecoveryDecision(TodoBoardPreparationResult prepareResult)
        {
            return prepareResult != null
                && (prepareResult.Status == TodoBoardPreparationStatus.RecoveryRequired
                    || prepareResult.Status == TodoBoardPreparationStatus.Paused);
        }
    }

    /// <summary>
    /// 描述 Todo 启动流程中的一次可观察更新。
    /// </summary>
    internal sealed class TodoRunStartupUpdate
    {
        internal TodoBoard Board { get; private set; }

        internal string ActivePlanFingerprint { get; private set; } = string.Empty;

        internal AgentEvent Event { get; private set; }

        internal bool RunStarted { get; private set; }

        internal bool ShouldStop { get; private set; }

        internal static TodoRunStartupUpdate PendingDecision(
            TodoBoard board,
            string activePlanFingerprint,
            AgentEvent agentEvent)
        {
            return Create(board, activePlanFingerprint, agentEvent, runStarted: false, shouldStop: false);
        }

        internal static TodoRunStartupUpdate Started(
            TodoBoard board,
            string activePlanFingerprint,
            AgentEvent agentEvent)
        {
            return Create(board, activePlanFingerprint, agentEvent, runStarted: true, shouldStop: false);
        }

        internal static TodoRunStartupUpdate Stop(
            TodoBoard board,
            string activePlanFingerprint,
            AgentEvent agentEvent)
        {
            return Create(board, activePlanFingerprint, agentEvent, runStarted: false, shouldStop: true);
        }

        private static TodoRunStartupUpdate Create(
            TodoBoard board,
            string activePlanFingerprint,
            AgentEvent agentEvent,
            bool runStarted,
            bool shouldStop)
        {
            return new TodoRunStartupUpdate
            {
                Board = board,
                ActivePlanFingerprint = activePlanFingerprint ?? string.Empty,
                Event = agentEvent,
                RunStarted = runStarted,
                ShouldStop = shouldStop
            };
        }
    }
}
