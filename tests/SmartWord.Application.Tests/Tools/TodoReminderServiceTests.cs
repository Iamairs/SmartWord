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
                RoundsSinceLastTodoUpdate = 8
            };

            var decision = service.BuildDecision(board, false);

            Assert.True(decision.ShouldInject);
            Assert.False(decision.IsHighPriority);
        }

        [Fact]
        public void BuildDecision_AfterFirstReminderRequiresTenRounds_ReturnsNoReminderBeforeCooldown()
        {
            var service = new TodoReminderService();
            var board = new TodoBoard
            {
                RoundsSinceLastTodoUpdate = 9,
                ReminderCount = 1,
                RoundsSinceLastReminder = 9
            };

            var decision = service.BuildDecision(board, false);

            Assert.False(decision.ShouldInject);
        }

        [Fact]
        public void BuildDecision_AfterFirstReminderAndTenRounds_ReturnsNormalReminder()
        {
            var service = new TodoReminderService();
            var board = new TodoBoard
            {
                RoundsSinceLastTodoUpdate = 18,
                ReminderCount = 1,
                RoundsSinceLastReminder = 10
            };

            var decision = service.BuildDecision(board, false);

            Assert.True(decision.ShouldInject);
            Assert.False(decision.IsHighPriority);
        }

        [Fact]
        public void BuildDecision_PendingWriteWithoutTodoWrite_DoesNotReturnHighPriorityReminder()
        {
            var service = new TodoReminderService();
            var board = new TodoBoard
            {
                HasPendingWriteWithoutTodoWrite = true,
                RoundsSincePendingWriteWithoutTodoWrite = 1
            };

            var decision = service.BuildDecision(board, true);

            Assert.False(decision.ShouldInject);
            Assert.False(decision.IsHighPriority);
        }
    }
}
