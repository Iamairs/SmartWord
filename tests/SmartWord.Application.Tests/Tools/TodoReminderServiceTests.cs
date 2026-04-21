using SmartWord.Application.Todo;
using SmartWord.Core.Models;
using Xunit;

namespace SmartWord.Application.Tests.Tools
{
    public sealed class TodoReminderServiceTests
    {
        [Fact]
        public void BuildDecision_ExplorationThresholdReached_ReturnsNormalReminder()
        {
            var service = new TodoReminderService();
            var board = new TodoBoard
            {
                RoundsSinceLastTodoUpdate = 5
            };

            var decision = service.BuildDecision(board, false);

            Assert.True(decision.ShouldInject);
            Assert.False(decision.IsHighPriority);
        }

        [Fact]
        public void BuildDecision_AfterFirstReminderRequiresThreeRounds_ReturnsNoReminderBeforeCooldown()
        {
            var service = new TodoReminderService();
            var board = new TodoBoard
            {
                RoundsSinceLastTodoUpdate = 6,
                ReminderCount = 1,
                RoundsSinceLastReminder = 2
            };

            var decision = service.BuildDecision(board, false);

            Assert.False(decision.ShouldInject);
        }

        [Fact]
        public void BuildDecision_AfterFirstReminderAndThreeRounds_ReturnsNormalReminder()
        {
            var service = new TodoReminderService();
            var board = new TodoBoard
            {
                RoundsSinceLastTodoUpdate = 6,
                ReminderCount = 1,
                RoundsSinceLastReminder = 3
            };

            var decision = service.BuildDecision(board, false);

            Assert.True(decision.ShouldInject);
            Assert.False(decision.IsHighPriority);
        }

        [Fact]
        public void BuildDecision_PendingWriteWithoutTodoWrite_ReturnsHighPriorityReminder()
        {
            var service = new TodoReminderService();
            var board = new TodoBoard
            {
                HasPendingWriteWithoutTodoWrite = true,
                RoundsSincePendingWriteWithoutTodoWrite = 1
            };

            var decision = service.BuildDecision(board, true);

            Assert.True(decision.ShouldInject);
            Assert.True(decision.IsHighPriority);
        }
    }
}
