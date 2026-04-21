using SmartWord.Core.Models;

namespace SmartWord.Application.Todo
{
    /// <summary>
    /// 统一封装 Todo reminder 的触发策略，避免编排器充斥魔法值。
    /// </summary>
    public sealed class TodoReminderService
    {
        public const int ExplorationThreshold = 5;
        public const int PostWriteThreshold = 3;
        public const int FirstReminderCooldown = 3;
        public const int RecurringReminderInterval = 5;

        public TodoReminderDecision BuildDecision(TodoBoard board, bool hasSuccessfulDocumentWriteOccurredInRun)
        {
            if (board == null)
            {
                return TodoReminderDecision.None;
            }

            if (board.HasPendingWriteWithoutTodoWrite
                && !board.HasInjectedPendingWriteReminder
                && board.RoundsSincePendingWriteWithoutTodoWrite >= 1)
            {
                return TodoReminderDecision.HighPriority(
                    "提醒：上一轮已经发生文档写入，但 todo board 尚未更新。请先执行 todo_write 同步当前步骤状态，再继续执行。");
            }

            if (!ShouldInjectScheduledReminder(board, hasSuccessfulDocumentWriteOccurredInRun))
            {
                return TodoReminderDecision.None;
            }

            return TodoReminderDecision.Normal(
                "提醒：当前任务较复杂，请持续维护 todo board。若计划已变化，请先读取或更新任务板，再继续执行。");
        }

        private static bool ShouldInjectScheduledReminder(
            TodoBoard board,
            bool hasSuccessfulDocumentWriteOccurredInRun)
        {
            if (board == null || board.RoundsSinceLastTodoUpdate <= 0)
            {
                return false;
            }

            if (board.ReminderCount <= 0)
            {
                var initialThreshold = hasSuccessfulDocumentWriteOccurredInRun
                    ? PostWriteThreshold
                    : ExplorationThreshold;
                return board.RoundsSinceLastTodoUpdate >= initialThreshold;
            }

            var cadence = board.ReminderCount == 1
                ? FirstReminderCooldown
                : RecurringReminderInterval;
            return board.RoundsSinceLastReminder >= cadence;
        }
    }

    public sealed class TodoReminderDecision
    {
        public static readonly TodoReminderDecision None = new TodoReminderDecision(false, false, string.Empty);

        private TodoReminderDecision(bool shouldInject, bool isHighPriority, string message)
        {
            ShouldInject = shouldInject;
            IsHighPriority = isHighPriority;
            Message = message ?? string.Empty;
        }

        public bool ShouldInject { get; }

        public bool IsHighPriority { get; }

        public string Message { get; }

        public static TodoReminderDecision Normal(string message)
        {
            return new TodoReminderDecision(true, false, message);
        }

        public static TodoReminderDecision HighPriority(string message)
        {
            return new TodoReminderDecision(true, true, message);
        }
    }
}
