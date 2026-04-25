using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Application.Todo;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.Persistence;
using Xunit;

namespace SmartWord.Application.Tests.Tools
{
    public class TodoManagerTests
    {
        [Fact]
        public async Task ApplyChangeAsync_SetSecondItemToInProgress_WhenAnotherActiveExists_Throws()
        {
            var manager = CreateManager();
            await manager.InitializeFromExecutionPlanAsync(
                "doc1",
                new ExecutionPlan
                {
                    TodoList = new List<TodoItem>
                    {
                        new TodoItem { Description = "第一步" },
                        new TodoItem { Description = "第二步" }
                    }
                },
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.ApplyChangeAsync(
                    "doc1",
                    new TodoWriteRequest
                    {
                        Action = "set_status",
                        Id = "T2",
                        Status = TodoItemStatus.InProgress
                    },
                    CancellationToken.None));

            Assert.Contains("in_progress", exception.Message);
        }

        [Fact]
        public async Task ApplyChangeAsync_ReplaceBoard_WithPendingItems_PromotesFirstPendingToActive()
        {
            var manager = CreateManager();
            var result = await manager.ApplyChangeAsync(
                "doc1",
                new TodoWriteRequest
                {
                    Action = "replace_board",
                    Items = new List<TodoBoardItem>
                    {
                        new TodoBoardItem { Id = "T1", Content = "第一步", Status = TodoItemStatus.Pending, Order = 1 },
                        new TodoBoardItem { Id = "T2", Content = "第二步", Status = TodoItemStatus.Pending, Order = 2 }
                    }
                },
                CancellationToken.None);

            Assert.Equal("T1", result.CurrentTodoId);
            Assert.Contains("InProgress", result.BoardJson);
        }

        [Fact]
        public async Task JsonTodoStore_SaveBoardAsync_CanRoundTripByDocumentPath()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "smartword-todo-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var store = new JsonTodoStore(tempDirectory);
                var board = new TodoBoard
                {
                    BoardId = "board-1",
                    DocumentPath = "doc1",
                    Items = new List<TodoBoardItem>
                    {
                        new TodoBoardItem { Id = "T1", Content = "第一步", Status = TodoItemStatus.InProgress, Order = 1 }
                    }
                };

                await store.SaveBoardAsync(board, CancellationToken.None);
                var reloaded = await store.GetBoardAsync("doc1", CancellationToken.None);

                Assert.NotNull(reloaded);
                Assert.Single(reloaded.Items);
                Assert.Equal("T1", reloaded.Items[0].Id);
                Assert.Equal(TodoItemStatus.InProgress, reloaded.Items[0].Status);
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
        }

        [Fact]
        public async Task ApplyChangeAsync_TodoWrite_ResetsReminderState()
        {
            var manager = CreateManager();
            await manager.InitializeFromExecutionPlanAsync(
                "doc1",
                new ExecutionPlan
                {
                    TodoList = new List<TodoItem>
                    {
                        new TodoItem { Description = "第一步" }
                    }
                },
                CancellationToken.None);

            await manager.RecordRoundWithoutTodoWriteAsync(
                "doc1",
                hasEffectiveExecutionRound: true,
                successfulDocumentWriteOccurred: true,
                cancellationToken: CancellationToken.None);
            await manager.MarkReminderInjectedAsync("doc1", true, CancellationToken.None);

            var result = await manager.ApplyChangeAsync(
                "doc1",
                new TodoWriteRequest
                {
                    Action = "set_status",
                    Id = "T1",
                    Status = TodoItemStatus.Completed
                },
                CancellationToken.None);

            Assert.Equal(0, result.Board.RoundsSinceLastTodoUpdate);
            Assert.Equal(0, result.Board.RoundsSinceLastReminder);
            Assert.Equal(0, result.Board.ReminderCount);
            Assert.False(result.Board.HasPendingWriteWithoutTodoWrite);
            Assert.False(result.Board.HasInjectedPendingWriteReminder);
        }

        [Fact]
        public async Task MarkRunSucceededAndDeleteAsync_AfterBoardCreated_DeletesBoardFile()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "smartword-todo-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var store = new JsonTodoStore(tempDirectory);
                var manager = new TodoManager(store);

                await manager.EnsureBoardAsync("doc1", CancellationToken.None);
                Assert.True(await store.ExistsAsync("doc1", CancellationToken.None));

                await manager.MarkRunSucceededAndDeleteAsync("doc1", CancellationToken.None);

                Assert.False(await store.ExistsAsync("doc1", CancellationToken.None));
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
        }

        [Fact]
        public async Task PrepareBoardForRunAsync_PreviousRunningBoard_ReturnsRecoveryRequired()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "smartword-todo-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var store = new JsonTodoStore(tempDirectory);
                var manager = new TodoManager(store);
                await store.SaveBoardAsync(
                    new TodoBoard
                    {
                        SchemaVersion = TodoBoard.CurrentSchemaVersion,
                        BoardId = "board-1",
                        DocumentPath = "doc1",
                        ExecutionState = TodoBoardExecutionState.Running,
                        LastRunId = "run-1",
                        Items = new List<TodoBoardItem>
                        {
                            new TodoBoardItem { Id = "T1", Content = "第一步", Status = TodoItemStatus.InProgress, Order = 1 }
                        }
                    },
                    CancellationToken.None);

                var result = await manager.PrepareBoardForRunAsync("doc1", null, CancellationToken.None);

                Assert.Equal(TodoBoardPreparationStatus.RecoveryRequired, result.Status);
                Assert.Equal(TodoBoardRunOutcome.CrashedLike, result.LastRunOutcome);
                Assert.Contains("疑似", result.RecoveryReason);
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
        }

        [Fact]
        public async Task ResolveRecoveryAsync_RebuildFromActivePlan_ReplacesBoardWithPlanItems()
        {
            var manager = CreateManager();
            await manager.EnsureBoardAsync("doc1", CancellationToken.None);

            var board = await manager.ResolveRecoveryAsync(
                "doc1",
                TodoBoardRecoveryDecision.RebuildFromActivePlan,
                new ExecutionPlan
                {
                    TodoList = new List<TodoItem>
                    {
                        new TodoItem { Description = "第一步" },
                        new TodoItem { Description = "第二步" }
                    }
                },
                CancellationToken.None);

            Assert.Equal(TodoBoardExecutionState.Idle, board.ExecutionState);
            Assert.Equal(2, board.Items.Count);
            Assert.Equal("T1", board.Items[0].Id);
            Assert.Equal(TodoItemStatus.InProgress, board.Items[0].Status);
        }

        [Fact]
        public async Task MarkRunPausedAsync_AfterBoardExists_PersistsPausedState()
        {
            var manager = CreateManager();
            await manager.InitializeFromExecutionPlanAsync(
                "doc1",
                new ExecutionPlan
                {
                    TodoList = new List<TodoItem>
                    {
                        new TodoItem { Description = "第一步" },
                        new TodoItem { Description = "第二步" }
                    }
                },
                CancellationToken.None);

            var pausedBoard = await manager.MarkRunPausedAsync(
                "doc1",
                "当前任务已达到本轮 100 轮预算上限，任务已暂停。",
                CancellationToken.None);
            var prepareResult = await manager.PrepareBoardForRunAsync("doc1", null, CancellationToken.None);

            Assert.Equal(TodoBoardExecutionState.Paused, pausedBoard.ExecutionState);
            Assert.Equal(TodoBoardRunOutcome.PausedByBudget, pausedBoard.LastRunOutcome);
            Assert.Contains("预算上限", pausedBoard.PauseReason);
            Assert.Equal(TodoBoardPreparationStatus.Paused, prepareResult.Status);
            Assert.Contains("预算上限", prepareResult.PauseReason);
        }

        [Fact]
        public async Task JsonTodoStore_GetBoardAsync_InvalidJson_ThrowsControlledError()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "smartword-todo-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var store = new JsonTodoStore(tempDirectory);
                var board = new TodoBoard
                {
                    BoardId = "board-1",
                    DocumentPath = "doc1"
                };

                await store.SaveBoardAsync(board, CancellationToken.None);
                var filePath = Directory.GetFiles(tempDirectory, "*.json")[0];
                File.WriteAllText(filePath, "{ invalid json", System.Text.Encoding.UTF8);

                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    store.GetBoardAsync("doc1", CancellationToken.None));
                Assert.Contains("已损坏", exception.Message);
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
        }

        private static TodoManager CreateManager()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "smartword-todo-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            return new TodoManager(new JsonTodoStore(tempDirectory));
        }
    }
}
