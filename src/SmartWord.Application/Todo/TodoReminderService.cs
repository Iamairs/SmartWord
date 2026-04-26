using SmartWord.Core.Models;

namespace SmartWord.Application.Todo
{
    /// <summary>
    /// 统一封装 Todo reminder 的触发策略，避免编排器充斥魔法值。
    /// </summary>
    public sealed class TodoReminderService
    {
        public const int ExplorationThreshold = 8;
        public const int RecurringReminderInterval = 10;

        public TodoReminderDecision BuildDecision(TodoBoard board, bool hasSuccessfulDocumentWriteOccurredInRun)
        {
            if (board == null)
            {
                return TodoReminderDecision.None;
            }

            if (!ShouldInjectScheduledReminder(board, hasSuccessfulDocumentWriteOccurredInRun))
            {
                return TodoReminderDecision.None;
            }

            return TodoReminderDecision.Normal(
                "内部提醒：当前任务较复杂，如阶段边界或计划已变化，请读取或更新 todo board 后再继续执行。");
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
                return board.RoundsSinceLastTodoUpdate >= ExplorationThreshold;

            return board.RoundsSinceLastReminder >= RecurringReminderInterval;
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
