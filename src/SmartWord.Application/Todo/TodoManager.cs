using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Todo
{
    /// <summary>
    /// 统一维护 Todo Board 的生命周期、业务规则、统计计算和展示视图。
    /// </summary>
    public sealed class TodoManager
    {
        private const int MaxItems = 20;
        private const string ActiveDocumentFallback = "__active_document__";
        private const string CorruptedBoardMessage = "Todo Board 文件已损坏，无法读取。";
        private static readonly Regex TodoIdRegex = new Regex("^[A-Za-z][A-Za-z0-9_-]{0,31}$", RegexOptions.Compiled);
        private static readonly JsonSerializerSettings BoardJsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new StringEnumConverter() }
        };

        private readonly AsyncLocal<string> _currentDocumentPath = new AsyncLocal<string>();
        private readonly ITodoStore _todoStore;

        public TodoManager(ITodoStore todoStore)
        {
            _todoStore = todoStore ?? throw new ArgumentNullException(nameof(todoStore));
        }

        public void SetCurrentDocumentPath(string documentPath)
        {
            _currentDocumentPath.Value = NormalizeDocumentPath(documentPath);
        }

        public string GetCurrentDocumentPathOrDefault()
        {
            return NormalizeDocumentPath(_currentDocumentPath.Value);
        }

        public async Task<TodoBoardPreparationResult> PrepareBoardForRunAsync(
            string documentPath,
            ExecutionPlan activePlan,
            CancellationToken cancellationToken)
        {
            var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
            var planFingerprint = ComputePlanFingerprint(activePlan);
            TodoBoard board = null;

            try
            {
                board = await _todoStore.GetBoardAsync(normalizedDocumentPath, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsCorruptedBoardException(ex))
            {
                return new TodoBoardPreparationResult
                {
                    Status = TodoBoardPreparationStatus.RecoveryRequired,
                    RecoveryReason = "检测到 Todo Board 文件已损坏，建议按当前计划重建，或丢弃后新建空任务板。",
                    LastRunOutcome = TodoBoardRunOutcome.Failed,
                    LastErrorSummary = ex.Message,
                    HasActivePlan = activePlan != null,
                    ActivePlanFingerprint = planFingerprint,
                    CanRecoverExisting = false
                };
            }

            if (board == null)
            {
                board = activePlan != null
                    ? CreateBoardFromExecutionPlan(normalizedDocumentPath, activePlan)
                    : CreateEmptyBoard(normalizedDocumentPath);
                board.SourcePlanFingerprint = string.IsNullOrWhiteSpace(planFingerprint)
                    ? board.SourcePlanFingerprint
                    : planFingerprint;
                await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
                return CreateReadyPreparationResult(board, activePlan, planFingerprint);
            }

            NormalizeBoard(board);
            if (ShouldTreatLegacyBoardAsDirty(board))
            {
                MarkBoardRecoveryRequired(
                    board,
                    TodoBoardRunOutcome.CrashedLike,
                    "检测到旧版本 Todo Board，无法确认其与当前文档是否一致，请先选择恢复旧板、按计划重建或丢弃后新建。",
                    "检测到旧版本 Todo Board，缺少运行态元数据。");
                await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
                return CreateRecoveryPreparationResult(board, activePlan, planFingerprint, canRecoverExisting: true);
            }

            if (board.ExecutionState == TodoBoardExecutionState.Running)
            {
                MarkBoardRecoveryRequired(
                    board,
                    TodoBoardRunOutcome.CrashedLike,
                    "检测到上一次 Agent 运行疑似在异常退出前停留在运行中状态，请先选择恢复方式。",
                    string.IsNullOrWhiteSpace(board.LastErrorSummary)
                        ? "上一次运行未正常完成收尾。"
                        : board.LastErrorSummary);
                await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
                return CreateRecoveryPreparationResult(board, activePlan, planFingerprint, canRecoverExisting: true);
            }

            if (board.ExecutionState == TodoBoardExecutionState.RecoveryRequired)
            {
                if (string.IsNullOrWhiteSpace(board.RecoveryReason))
                {
                    board.RecoveryReason = "检测到上一轮执行未正常结束，请先确认是否恢复旧任务板。";
                    await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
                }

                return CreateRecoveryPreparationResult(board, activePlan, planFingerprint, canRecoverExisting: true);
            }

            if (board.ExecutionState == TodoBoardExecutionState.Paused)
            {
                if (string.IsNullOrWhiteSpace(board.PauseReason))
                {
                    board.PauseReason = "上一次 Agent 运行达到本轮预算上限，任务已暂停，可选择继续、重建或丢弃后重来。";
                    await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
                }

                return CreatePausedPreparationResult(board, activePlan, planFingerprint, canRecoverExisting: true);
            }

            if (board.SchemaVersion < TodoBoard.CurrentSchemaVersion && board.Items.Count == 0)
            {
                board.SchemaVersion = TodoBoard.CurrentSchemaVersion;
                await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
            }

            return CreateReadyPreparationResult(board, activePlan, planFingerprint);
        }

        public async Task<TodoBoard> MarkRunStartedAsync(
            string documentPath,
            string runId,
            string activePlanFingerprint,
            CancellationToken cancellationToken)
        {
            var board = await EnsureBoardAsync(documentPath, cancellationToken).ConfigureAwait(false);
            board.SchemaVersion = TodoBoard.CurrentSchemaVersion;
            board.ExecutionState = TodoBoardExecutionState.Running;
            board.LastRunId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId.Trim();
            board.LastRunStartedAtUtc = DateTime.UtcNow;
            board.LastRunFinishedAtUtc = null;
            board.LastRunOutcome = TodoBoardRunOutcome.None;
            board.LastErrorSummary = string.Empty;
            board.RecoveryReason = string.Empty;
            board.PauseReason = string.Empty;
            if (!string.IsNullOrWhiteSpace(activePlanFingerprint))
            {
                board.SourcePlanFingerprint = activePlanFingerprint;
            }

            await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
            return board;
        }

        public async Task<TodoBoard> MarkRunPausedAsync(
            string documentPath,
            string reason,
            CancellationToken cancellationToken)
        {
            var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
            TodoBoard board;

            try
            {
                board = await _todoStore.GetBoardAsync(normalizedDocumentPath, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsCorruptedBoardException(ex))
            {
                board = CreateEmptyBoard(normalizedDocumentPath);
                board.LastErrorSummary = ex.Message;
            }

            board = board ?? CreateEmptyBoard(normalizedDocumentPath);
            NormalizeBoard(board);
            MarkBoardPaused(board, reason);
            await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
            return board;
        }

        public Task MarkRunSucceededAndDeleteAsync(string documentPath, CancellationToken cancellationToken)
        {
            return _todoStore.DeleteBoardAsync(NormalizeDocumentPath(documentPath), cancellationToken);
        }

        public async Task<TodoBoard> MarkRunInterruptedAsync(
            string documentPath,
            TodoBoardRunOutcome outcome,
            string reason,
            CancellationToken cancellationToken)
        {
            var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
            TodoBoard board;

            try
            {
                board = await _todoStore.GetBoardAsync(normalizedDocumentPath, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsCorruptedBoardException(ex))
            {
                board = CreateEmptyBoard(normalizedDocumentPath);
                board.LastErrorSummary = ex.Message;
            }

            board = board ?? CreateEmptyBoard(normalizedDocumentPath);
            NormalizeBoard(board);
            MarkBoardRecoveryRequired(
                board,
                outcome,
                BuildRecoveryReason(outcome, reason),
                reason);
            await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
            return board;
        }

        public async Task<TodoBoard> ResolveRecoveryAsync(
            string documentPath,
            TodoBoardRecoveryDecision decision,
            ExecutionPlan activePlan,
            CancellationToken cancellationToken)
        {
            var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
            var activePlanFingerprint = ComputePlanFingerprint(activePlan);

            switch (decision)
            {
                case TodoBoardRecoveryDecision.RecoverExisting:
                {
                    var board = await GetBoardAsync(normalizedDocumentPath, cancellationToken).ConfigureAwait(false);
                    if (board == null)
                    {
                        throw new InvalidOperationException("当前不存在可恢复的 Todo Board。");
                    }

                    board.ExecutionState = TodoBoardExecutionState.Idle;
                    board.RecoveryReason = string.Empty;
                    board.PauseReason = string.Empty;
                    if (!string.IsNullOrWhiteSpace(activePlanFingerprint))
                    {
                        board.SourcePlanFingerprint = activePlanFingerprint;
                    }

                    await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
                    return board;
                }
                case TodoBoardRecoveryDecision.RebuildFromActivePlan:
                {
                    if (activePlan == null)
                    {
                        throw new InvalidOperationException("当前没有可用于重建 Todo Board 的 ActivePlan。");
                    }

                    var board = CreateBoardFromExecutionPlan(normalizedDocumentPath, activePlan);
                    board.SourcePlanFingerprint = activePlanFingerprint;
                    await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
                    return board;
                }
                case TodoBoardRecoveryDecision.DiscardAndCreateEmpty:
                {
                    await _todoStore.DeleteBoardAsync(normalizedDocumentPath, cancellationToken).ConfigureAwait(false);
                    var board = CreateEmptyBoard(normalizedDocumentPath);
                    board.SourcePlanFingerprint = activePlanFingerprint;
                    await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
                    return board;
                }
                default:
                    throw new InvalidOperationException("未知的 Todo Board 恢复决策。");
            }
        }

        public Task DiscardBoardAsync(string documentPath, CancellationToken cancellationToken)
        {
            return _todoStore.DeleteBoardAsync(NormalizeDocumentPath(documentPath), cancellationToken);
        }

        public async Task<TodoBoard> EnsureBoardAsync(string documentPath, CancellationToken cancellationToken)
        {
            var normalizedDocumentPath = NormalizeDocumentPath(documentPath);
            var board = await GetBoardAsync(normalizedDocumentPath, cancellationToken).ConfigureAwait(false);
            if (board != null)
            {
                return board;
            }

            var created = CreateEmptyBoard(normalizedDocumentPath);
            await _todoStore.SaveBoardAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }

        public async Task<TodoBoard> GetBoardAsync(string documentPath, CancellationToken cancellationToken)
        {
            var board = await _todoStore
                .GetBoardAsync(NormalizeDocumentPath(documentPath), cancellationToken)
                .ConfigureAwait(false);
            if (board != null)
            {
                NormalizeBoard(board);
            }

            return board;
        }

        public async Task<TodoWriteResult> InitializeFromExecutionPlanAsync(
            string documentPath,
            ExecutionPlan plan,
            CancellationToken cancellationToken)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var board = CreateBoardFromExecutionPlan(documentPath, plan);
            await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
            return CreateResult(board, "replace_board", "已根据当前执行计划初始化 Todo Board。", string.Empty);
        }

        public async Task<TodoWriteResult> ApplyChangeAsync(
            string documentPath,
            TodoWriteRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var board = await EnsureBoardAsync(documentPath, cancellationToken).ConfigureAwait(false);
            var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
            string updatedItemId;
            string message;

            switch (action)
            {
                case "reset_board":
                    board.Items = new List<TodoBoardItem>();
                    updatedItemId = string.Empty;
                    message = "已清空当前 Todo Board。";
                    break;
                case "add_item":
                    updatedItemId = AddItem(board, request);
                    message = $"已新增任务 {updatedItemId}。";
                    break;
                case "update_item":
                    updatedItemId = UpdateItem(board, request);
                    message = $"已更新任务 {updatedItemId}。";
                    break;
                case "set_status":
                    updatedItemId = SetItemStatus(board, request);
                    message = $"已更新任务 {updatedItemId} 的状态。";
                    break;
                case "remove_item":
                    updatedItemId = RemoveItem(board, request);
                    message = $"已删除任务 {updatedItemId}。";
                    break;
                case "reorder_items":
                    updatedItemId = ReorderItems(board, request);
                    message = "已重排任务顺序。";
                    break;
                case "replace_board":
                    updatedItemId = ReplaceBoard(board, request);
                    message = "已整体替换 Todo Board。";
                    break;
                default:
                    throw new InvalidOperationException("TodoWrite.action 非法。允许的动作：reset_board、add_item、update_item、set_status、remove_item、reorder_items、replace_board。");
            }

            NormalizeBoard(board);
            ValidateBoard(board.Items);
            EnsureSingleActiveItem(board.Items, action == "replace_board");
            StampBoard(board);
            await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
            return CreateResult(board, action, message, updatedItemId);
        }

        public async Task<TodoBoard> RecordRoundWithoutTodoWriteAsync(
            string documentPath,
            bool hasEffectiveExecutionRound,
            bool successfulDocumentWriteOccurred,
            CancellationToken cancellationToken)
        {
            var board = await EnsureBoardAsync(documentPath, cancellationToken).ConfigureAwait(false);
            if (hasEffectiveExecutionRound)
            {
                board.RoundsSinceLastTodoUpdate++;
                board.RoundsSinceLastReminder++;
            }

            if (successfulDocumentWriteOccurred)
            {
                board.HasPendingWriteWithoutTodoWrite = true;
                board.RoundsSincePendingWriteWithoutTodoWrite = 0;
                board.HasInjectedPendingWriteReminder = false;
            }
            else if (board.HasPendingWriteWithoutTodoWrite)
            {
                board.RoundsSincePendingWriteWithoutTodoWrite++;
            }

            board.UpdatedAt = DateTime.UtcNow;
            await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
            return board;
        }

        public async Task<TodoBoard> MarkReminderInjectedAsync(
            string documentPath,
            bool isHighPriority,
            CancellationToken cancellationToken)
        {
            var board = await EnsureBoardAsync(documentPath, cancellationToken).ConfigureAwait(false);
            board.LastReminderRound = board.RoundsSinceLastTodoUpdate;
            board.RoundsSinceLastReminder = 0;
            board.ReminderCount++;
            if (isHighPriority && board.HasPendingWriteWithoutTodoWrite)
            {
                board.HasInjectedPendingWriteReminder = true;
            }

            board.UpdatedAt = DateTime.UtcNow;
            await _todoStore.SaveBoardAsync(board, cancellationToken).ConfigureAwait(false);
            return board;
        }

        public TodoBoardStats BuildStats(TodoBoard board)
        {
            var stats = new TodoBoardStats();
            if (board == null || board.Items == null)
            {
                return stats;
            }

            foreach (var item in board.Items)
            {
                switch (item.Status)
                {
                    case TodoItemStatus.Pending:
                        stats.PendingCount++;
                        break;
                    case TodoItemStatus.InProgress:
                        stats.InProgressCount++;
                        stats.CurrentTodoId = item.Id ?? string.Empty;
                        stats.CurrentTodoContent = item.Content ?? string.Empty;
                        break;
                    case TodoItemStatus.Completed:
                        stats.CompletedCount++;
                        break;
                    case TodoItemStatus.Failed:
                        stats.FailedCount++;
                        break;
                    case TodoItemStatus.Skipped:
                        stats.SkippedCount++;
                        break;
                }
            }

            stats.TotalCount = board.Items.Count;
            stats.HandledCount = stats.CompletedCount + stats.SkippedCount;
            return stats;
        }

        public string BuildMarkdownView(TodoBoard board)
        {
            var builder = new StringBuilder();
            var stats = BuildStats(board);
            builder.AppendLine("# Todo Board");
            builder.AppendLine();

            if (board == null || board.Items == null || board.Items.Count == 0)
            {
                builder.AppendLine("- [ ] 当前尚未建立任务项");
            }
            else
            {
                foreach (var item in board.Items.OrderBy(i => i.Order))
                {
                    builder.Append("- ");
                    builder.Append(GetStatusMarker(item.Status));
                    builder.Append(' ');
                    builder.Append(item.Id);
                    builder.Append(' ');
                    builder.AppendLine(item.Content);

                    if (!string.IsNullOrWhiteSpace(item.Notes))
                    {
                        builder.AppendLine("  备注：" + item.Notes);
                    }
                }
            }

            builder.AppendLine();
            builder.AppendLine(
                $"统计：total={stats.TotalCount}, pending={stats.PendingCount}, in_progress={stats.InProgressCount}, completed={stats.CompletedCount}, failed={stats.FailedCount}, skipped={stats.SkippedCount}");
            if (!string.IsNullOrWhiteSpace(stats.CurrentTodoId))
            {
                builder.AppendLine($"当前任务：{stats.CurrentTodoId} {stats.CurrentTodoContent}");
            }

            return builder.ToString().TrimEnd();
        }

        public string BuildPromptBlock(TodoBoard board)
        {
            var markdown = BuildMarkdownView(board);
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return string.Empty;
            }

            return "--- TODO BOARD ---" + Environment.NewLine + markdown;
        }

        public string SerializeBoard(TodoBoard board)
        {
            return JsonConvert.SerializeObject(board, BoardJsonSettings);
        }

        public string ComputePlanFingerprint(ExecutionPlan plan)
        {
            if (plan == null)
            {
                return string.Empty;
            }

            var payload = JsonConvert.SerializeObject(plan, Formatting.None);
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                var hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var item in hash)
                {
                    builder.Append(item.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private TodoWriteResult CreateResult(TodoBoard board, string operation, string message, string updatedItemId)
        {
            var stats = BuildStats(board);
            return new TodoWriteResult
            {
                Success = true,
                Operation = operation ?? string.Empty,
                Message = message ?? string.Empty,
                UpdatedItemId = updatedItemId ?? string.Empty,
                CurrentTodoId = stats.CurrentTodoId ?? string.Empty,
                Board = board,
                Stats = stats,
                BoardJson = SerializeBoard(board),
                MarkdownView = BuildMarkdownView(board)
            };
        }

        private static TodoBoardPreparationResult CreateReadyPreparationResult(
            TodoBoard board,
            ExecutionPlan activePlan,
            string activePlanFingerprint)
        {
            return new TodoBoardPreparationResult
            {
                Status = TodoBoardPreparationStatus.Ready,
                Board = board,
                HasActivePlan = activePlan != null,
                ActivePlanFingerprint = activePlanFingerprint
            };
        }

        private static TodoBoardPreparationResult CreateRecoveryPreparationResult(
            TodoBoard board,
            ExecutionPlan activePlan,
            string activePlanFingerprint,
            bool canRecoverExisting)
        {
            return new TodoBoardPreparationResult
            {
                Status = TodoBoardPreparationStatus.RecoveryRequired,
                Board = board,
                RecoveryReason = board == null ? string.Empty : board.RecoveryReason,
                PauseReason = board == null ? string.Empty : board.PauseReason,
                LastRunOutcome = board == null ? TodoBoardRunOutcome.None : board.LastRunOutcome,
                LastErrorSummary = board == null ? string.Empty : board.LastErrorSummary,
                HasActivePlan = activePlan != null,
                ActivePlanFingerprint = activePlanFingerprint,
                CanRecoverExisting = canRecoverExisting
            };
        }

        private static TodoBoardPreparationResult CreatePausedPreparationResult(
            TodoBoard board,
            ExecutionPlan activePlan,
            string activePlanFingerprint,
            bool canRecoverExisting)
        {
            return new TodoBoardPreparationResult
            {
                Status = TodoBoardPreparationStatus.Paused,
                Board = board,
                RecoveryReason = board == null ? string.Empty : board.RecoveryReason,
                PauseReason = board == null ? string.Empty : board.PauseReason,
                LastRunOutcome = board == null ? TodoBoardRunOutcome.None : board.LastRunOutcome,
                LastErrorSummary = board == null ? string.Empty : board.LastErrorSummary,
                HasActivePlan = activePlan != null,
                ActivePlanFingerprint = activePlanFingerprint,
                CanRecoverExisting = canRecoverExisting
            };
        }

        private static TodoBoard CreateEmptyBoard(string documentPath)
        {
            var now = DateTime.UtcNow;
            return new TodoBoard
            {
                SchemaVersion = TodoBoard.CurrentSchemaVersion,
                BoardId = Guid.NewGuid().ToString("N"),
                DocumentPath = NormalizeDocumentPath(documentPath),
                Version = 1,
                UpdatedAt = now,
                ExecutionState = TodoBoardExecutionState.Idle,
                Items = new List<TodoBoardItem>()
            };
        }

        private static TodoBoard CreateBoardFromExecutionPlan(string documentPath, ExecutionPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var board = CreateEmptyBoard(documentPath);
            board.Items = new List<TodoBoardItem>();
            var now = DateTime.UtcNow;

            for (var index = 0; index < plan.TodoList.Count; index++)
            {
                var planItem = plan.TodoList[index] ?? new TodoItem();
                board.Items.Add(new TodoBoardItem
                {
                    Id = $"T{index + 1}",
                    Content = string.IsNullOrWhiteSpace(planItem.Description) ? $"步骤 {index + 1}" : planItem.Description.Trim(),
                    Status = planItem.Status,
                    Order = index + 1,
                    Notes = string.Empty,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CompletedAt = planItem.Status == TodoItemStatus.Completed ? (DateTime?)now : null
                });
            }

            ValidateBoard(board.Items);
            EnsureSingleActiveItem(board.Items, true);
            StampBoard(board);
            board.ExecutionState = TodoBoardExecutionState.Idle;
            board.LastRunOutcome = TodoBoardRunOutcome.None;
            board.LastRunStartedAtUtc = null;
            board.LastRunFinishedAtUtc = null;
            board.LastRunId = string.Empty;
            board.RecoveryReason = string.Empty;
            board.PauseReason = string.Empty;
            board.LastErrorSummary = string.Empty;
            return board;
        }

        private string AddItem(TodoBoard board, TodoWriteRequest request)
        {
            ValidateRequiredId(request.Id, "add_item");
            ValidateContent(request.Content, "add_item");
            EnsureItemCountWithinLimit(board.Items.Count + 1);
            if (board.Items.Any(item => string.Equals(item.Id, request.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("TodoWrite.add_item 的 id 已存在，不允许重复条目。");
            }

            var order = request.Order ?? (board.Items.Count == 0 ? 1 : board.Items.Max(item => item.Order) + 1);
            var item = new TodoBoardItem
            {
                Id = request.Id.Trim(),
                Content = request.Content.Trim(),
                Notes = (request.Notes ?? string.Empty).Trim(),
                Status = request.Status ?? TodoItemStatus.Pending,
                Order = order,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                CompletedAt = request.Status == TodoItemStatus.Completed ? (DateTime?)DateTime.UtcNow : null
            };

            board.Items.Add(item);
            NormalizeBoard(board);
            return item.Id;
        }

        private string UpdateItem(TodoBoard board, TodoWriteRequest request)
        {
            var item = FindExistingItem(board, request.Id, "update_item");
            if (!string.IsNullOrWhiteSpace(request.Content))
            {
                ValidateContent(request.Content, "update_item");
                item.Content = request.Content.Trim();
            }

            if (request.Notes != null)
            {
                item.Notes = request.Notes.Trim();
            }

            if (request.Order.HasValue)
            {
                item.Order = request.Order.Value;
            }

            if (request.Status.HasValue)
            {
                item.Status = request.Status.Value;
                item.CompletedAt = request.Status.Value == TodoItemStatus.Completed ? (DateTime?)DateTime.UtcNow : null;
            }

            item.UpdatedAt = DateTime.UtcNow;
            return item.Id;
        }

        private string SetItemStatus(TodoBoard board, TodoWriteRequest request)
        {
            if (!request.Status.HasValue)
            {
                throw new InvalidOperationException("TodoWrite.set_status 必须提供 status。");
            }

            var item = FindExistingItem(board, request.Id, "set_status");
            if (request.Status.Value == TodoItemStatus.InProgress)
            {
                var otherActive = board.Items.FirstOrDefault(candidate =>
                    !ReferenceEquals(candidate, item) && candidate.Status == TodoItemStatus.InProgress);
                if (otherActive != null)
                {
                    throw new InvalidOperationException("当前已存在另一条 in_progress 任务。请先将其改为 completed / failed / skipped / pending。");
                }
            }

            item.Status = request.Status.Value;
            item.UpdatedAt = DateTime.UtcNow;
            item.CompletedAt = request.Status.Value == TodoItemStatus.Completed ? (DateTime?)DateTime.UtcNow : null;

            if (request.Status.Value == TodoItemStatus.Completed || request.Status.Value == TodoItemStatus.Skipped)
            {
                AutoAdvanceNextPending(board);
            }

            return item.Id;
        }

        private string RemoveItem(TodoBoard board, TodoWriteRequest request)
        {
            var item = FindExistingItem(board, request.Id, "remove_item");
            var removedId = item.Id;
            var removedWasActive = item.Status == TodoItemStatus.InProgress;
            board.Items.Remove(item);
            NormalizeBoard(board);
            if (removedWasActive)
            {
                AutoAdvanceNextPending(board);
            }

            return removedId;
        }

        private string ReorderItems(TodoBoard board, TodoWriteRequest request)
        {
            if (request.OrderedIds == null || request.OrderedIds.Count == 0)
            {
                throw new InvalidOperationException("TodoWrite.reorder_items 必须提供 ordered_ids。");
            }

            var normalizedOrderedIds = request.OrderedIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToList();
            if (normalizedOrderedIds.Count != board.Items.Count)
            {
                throw new InvalidOperationException("TodoWrite.reorder_items 提供的 ordered_ids 数量必须与当前条目数完全一致。");
            }

            if (normalizedOrderedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedOrderedIds.Count)
            {
                throw new InvalidOperationException("TodoWrite.reorder_items 的 ordered_ids 中存在重复 id。");
            }

            for (var index = 0; index < normalizedOrderedIds.Count; index++)
            {
                var item = board.Items.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, normalizedOrderedIds[index], StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    throw new InvalidOperationException("TodoWrite.reorder_items 包含不存在的 id。");
                }

                item.Order = index + 1;
                item.UpdatedAt = DateTime.UtcNow;
            }

            NormalizeBoard(board);
            return string.Empty;
        }

        private string ReplaceBoard(TodoBoard board, TodoWriteRequest request)
        {
            if (request.Items == null)
            {
                throw new InvalidOperationException("TodoWrite.replace_board 必须提供 items。");
            }

            if (request.Items.Count > MaxItems)
            {
                throw new InvalidOperationException("TodoWrite.replace_board 超出最大 20 条限制。");
            }

            var now = DateTime.UtcNow;
            var items = request.Items.Select((item, index) => new TodoBoardItem
            {
                Id = (item == null ? string.Empty : item.Id ?? string.Empty).Trim(),
                Content = (item == null ? string.Empty : item.Content ?? string.Empty).Trim(),
                Notes = item == null ? string.Empty : (item.Notes ?? string.Empty).Trim(),
                Status = item == null ? TodoItemStatus.Pending : item.Status,
                Order = item != null && item.Order > 0 ? item.Order : index + 1,
                CreatedAt = item != null && item.CreatedAt != default(DateTime) ? item.CreatedAt : now,
                UpdatedAt = now,
                CompletedAt = item != null ? item.CompletedAt : null
            }).ToList();

            ValidateBoard(items);
            board.Items = items;
            NormalizeBoard(board);
            return string.Empty;
        }

        private static TodoBoardItem FindExistingItem(TodoBoard board, string id, string action)
        {
            ValidateRequiredId(id, action);
            var item = board.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                throw new InvalidOperationException($"TodoWrite.{action} 指向的 id 不存在。");
            }

            return item;
        }

        private static void ValidateBoard(IList<TodoBoardItem> items)
        {
            if (items == null)
            {
                throw new InvalidOperationException("Todo Board 条目集合不能为空。");
            }

            EnsureItemCountWithinLimit(items.Count);
            var idSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item == null)
                {
                    throw new InvalidOperationException("Todo Board 不允许包含空条目。");
                }

                ValidateRequiredId(item.Id, "item");
                ValidateContent(item.Content, "item");
                if (item.Order <= 0)
                {
                    throw new InvalidOperationException("Todo Board 中的 order 必须为正整数。");
                }

                if (!idSet.Add(item.Id.Trim()))
                {
                    throw new InvalidOperationException("Todo Board 中存在重复 id。");
                }
            }
        }

        private static void EnsureSingleActiveItem(IList<TodoBoardItem> items, bool forceFirstPendingToActive)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            var activeItems = items.Where(item => item.Status == TodoItemStatus.InProgress).ToList();
            if (activeItems.Count > 1)
            {
                throw new InvalidOperationException("Todo Board 同时只能存在一条 in_progress 任务。");
            }

            if (activeItems.Count == 0 && forceFirstPendingToActive)
            {
                var pending = items
                    .OrderBy(item => item.Order)
                    .FirstOrDefault(item => item.Status == TodoItemStatus.Pending);
                if (pending != null)
                {
                    pending.Status = TodoItemStatus.InProgress;
                    pending.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        private static void AutoAdvanceNextPending(TodoBoard board)
        {
            if (board.Items.Any(item => item.Status == TodoItemStatus.InProgress))
            {
                return;
            }

            var nextPending = board.Items
                .OrderBy(item => item.Order)
                .FirstOrDefault(item => item.Status == TodoItemStatus.Pending);
            if (nextPending != null)
            {
                nextPending.Status = TodoItemStatus.InProgress;
                nextPending.UpdatedAt = DateTime.UtcNow;
            }
        }

        private static void NormalizeBoard(TodoBoard board)
        {
            if (board == null)
            {
                return;
            }

            board.DocumentPath = NormalizeDocumentPath(board.DocumentPath);
            board.BoardId = board.BoardId ?? string.Empty;
            board.LastRunId = board.LastRunId ?? string.Empty;
            board.LastErrorSummary = board.LastErrorSummary ?? string.Empty;
            board.RecoveryReason = board.RecoveryReason ?? string.Empty;
            board.PauseReason = board.PauseReason ?? string.Empty;
            board.SourcePlanFingerprint = board.SourcePlanFingerprint ?? string.Empty;
            board.Items = (board.Items ?? new List<TodoBoardItem>())
                .OrderBy(item => item == null ? int.MaxValue : item.Order)
                .ThenBy(item => item == null ? string.Empty : item.Id)
                .ToList();
            for (var index = 0; index < board.Items.Count; index++)
            {
                board.Items[index].Order = index + 1;
                board.Items[index].Id = board.Items[index].Id ?? string.Empty;
                board.Items[index].Content = board.Items[index].Content ?? string.Empty;
                board.Items[index].Notes = board.Items[index].Notes ?? string.Empty;
            }
        }

        private static void StampBoard(TodoBoard board)
        {
            board.SchemaVersion = TodoBoard.CurrentSchemaVersion;
            board.Version = board.Version <= 0 ? 1 : board.Version + 1;
            board.UpdatedAt = DateTime.UtcNow;
            board.RoundsSinceLastTodoUpdate = 0;
            board.LastReminderRound = 0;
            board.RoundsSinceLastReminder = 0;
            board.ReminderCount = 0;
            board.HasPendingWriteWithoutTodoWrite = false;
            board.RoundsSincePendingWriteWithoutTodoWrite = 0;
            board.HasInjectedPendingWriteReminder = false;
        }

        private static void ValidateRequiredId(string id, string action)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException($"TodoWrite.{action} 必须提供非空 id。");
            }

            if (!TodoIdRegex.IsMatch(id.Trim()))
            {
                throw new InvalidOperationException("Todo id 非法。建议使用 T1、T2、A_step_1 这类短 id。");
            }
        }

        private static void ValidateContent(string content, string action)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException($"TodoWrite.{action} 不允许写入空任务内容。");
            }
        }

        private static void EnsureItemCountWithinLimit(int count)
        {
            if (count > MaxItems)
            {
                throw new InvalidOperationException("Todo Board 最多只允许维护 20 条任务。");
            }
        }

        private static string GetStatusMarker(TodoItemStatus status)
        {
            switch (status)
            {
                case TodoItemStatus.InProgress:
                    return "[~]";
                case TodoItemStatus.Completed:
                    return "[x]";
                case TodoItemStatus.Failed:
                    return "[!]";
                case TodoItemStatus.Skipped:
                    return "[-]";
                case TodoItemStatus.Pending:
                default:
                    return "[ ]";
            }
        }

        private static bool ShouldTreatLegacyBoardAsDirty(TodoBoard board)
        {
            return board != null
                && board.SchemaVersion < TodoBoard.CurrentSchemaVersion
                && board.Items != null
                && board.Items.Count > 0;
        }

        private static void MarkBoardRecoveryRequired(
            TodoBoard board,
            TodoBoardRunOutcome outcome,
            string recoveryReason,
            string errorSummary)
        {
            if (board == null)
            {
                return;
            }

            board.SchemaVersion = TodoBoard.CurrentSchemaVersion;
            board.ExecutionState = TodoBoardExecutionState.RecoveryRequired;
            board.LastRunFinishedAtUtc = DateTime.UtcNow;
            board.LastRunOutcome = outcome;
            board.RecoveryReason = string.IsNullOrWhiteSpace(recoveryReason) ? string.Empty : recoveryReason.Trim();
            board.PauseReason = string.Empty;
            if (!string.IsNullOrWhiteSpace(errorSummary))
            {
                board.LastErrorSummary = errorSummary.Trim();
            }

            board.UpdatedAt = DateTime.UtcNow;
        }

        private static void MarkBoardPaused(TodoBoard board, string pauseReason)
        {
            if (board == null)
            {
                return;
            }

            board.SchemaVersion = TodoBoard.CurrentSchemaVersion;
            board.ExecutionState = TodoBoardExecutionState.Paused;
            board.LastRunFinishedAtUtc = DateTime.UtcNow;
            board.LastRunOutcome = TodoBoardRunOutcome.PausedByBudget;
            board.RecoveryReason = string.Empty;
            board.PauseReason = string.IsNullOrWhiteSpace(pauseReason)
                ? "当前任务达到本轮预算上限，任务板已暂停，可在确认后继续。"
                : pauseReason.Trim();
            board.LastErrorSummary = string.Empty;
            board.UpdatedAt = DateTime.UtcNow;
        }

        private static string BuildRecoveryReason(TodoBoardRunOutcome outcome, string reason)
        {
            var detail = string.IsNullOrWhiteSpace(reason) ? string.Empty : " 原因：" + reason.Trim();
            switch (outcome)
            {
                case TodoBoardRunOutcome.Cancelled:
                    return "上一次 Agent 运行已被取消，任务板已保留，请确认是继续恢复还是重建。" + detail;
                case TodoBoardRunOutcome.RolledBack:
                    return "上一次 Agent 运行在写入后进入回滚/待修复终止，Todo Board 可能早于文档现状，请先选择恢复方式。" + detail;
                case TodoBoardRunOutcome.CrashedLike:
                    return "检测到上一次 Agent 运行疑似异常退出，请先确认是否恢复旧任务板。" + detail;
                case TodoBoardRunOutcome.Failed:
                default:
                    return "上一次 Agent 运行发生异常并提前结束，请先确认是否恢复旧任务板。" + detail;
            }
        }

        private static bool IsCorruptedBoardException(Exception ex)
        {
            return ex != null
                && !string.IsNullOrWhiteSpace(ex.Message)
                && ex.Message.IndexOf(CorruptedBoardMessage, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeDocumentPath(string documentPath)
        {
            return string.IsNullOrWhiteSpace(documentPath)
                ? ActiveDocumentFallback
                : documentPath.Trim();
        }
    }
}
