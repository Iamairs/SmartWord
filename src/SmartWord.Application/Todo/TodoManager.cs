using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 统一维护 Todo Board 的业务规则、统计计算和展示视图。
    /// </summary>
    public sealed class TodoManager
    {
        private const int MaxItems = 20;
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
            _currentDocumentPath.Value = string.IsNullOrWhiteSpace(documentPath)
                ? "__active_document__"
                : documentPath;
        }

        public string GetCurrentDocumentPathOrDefault()
        {
            return string.IsNullOrWhiteSpace(_currentDocumentPath.Value)
                ? "__active_document__"
                : _currentDocumentPath.Value;
        }

        public async Task<TodoBoard> EnsureBoardAsync(string documentPath, CancellationToken cancellationToken)
        {
            var board = await _todoStore.GetBoardAsync(documentPath, cancellationToken).ConfigureAwait(false);
            if (board != null)
            {
                NormalizeBoard(board);
                return board;
            }

            var created = CreateEmptyBoard(documentPath);
            await _todoStore.SaveBoardAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }

        public async Task<TodoBoard> GetBoardAsync(string documentPath, CancellationToken cancellationToken)
        {
            var board = await _todoStore.GetBoardAsync(documentPath, cancellationToken).ConfigureAwait(false);
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

            var board = CreateEmptyBoard(documentPath);
            board.Items = new List<TodoBoardItem>();

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
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    CompletedAt = planItem.Status == TodoItemStatus.Completed ? (DateTime?)DateTime.UtcNow : null
                });
            }

            ValidateBoard(board.Items);
            EnsureSingleActiveItem(board.Items, true);
            StampBoard(board);
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

        private static TodoBoard CreateEmptyBoard(string documentPath)
        {
            var now = DateTime.UtcNow;
            return new TodoBoard
            {
                BoardId = Guid.NewGuid().ToString("N"),
                DocumentPath = string.IsNullOrWhiteSpace(documentPath) ? "__active_document__" : documentPath,
                Version = 1,
                UpdatedAt = now,
                Items = new List<TodoBoardItem>()
            };
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

            board.DocumentPath = string.IsNullOrWhiteSpace(board.DocumentPath)
                ? "__active_document__"
                : board.DocumentPath;
            board.Items = (board.Items ?? new List<TodoBoardItem>())
                .OrderBy(item => item == null ? int.MaxValue : item.Order)
                .ThenBy(item => item == null ? string.Empty : item.Id)
                .ToList();
            for (var index = 0; index < board.Items.Count; index++)
            {
                board.Items[index].Order = index + 1;
            }
        }

        private static void StampBoard(TodoBoard board)
        {
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
    }
}
