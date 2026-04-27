using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.Persistence;
using Xunit;

namespace SmartWord.Application.Tests.Infrastructure
{
    public sealed class SqlitePersistenceTests
    {
        [Fact]
        public async Task AppendUserMessageAsync_ReloadStore_ReturnsPersistedMessage()
        {
            var databasePath = CreateTempDatabasePath();
            try
            {
                var documentPath = @"C:\docs\contract.docx";
                var database = new SmartWordSqliteDatabase(databasePath);
                var store = new SqliteConversationStore(database);

                await store.AppendUserMessageAsync(
                    documentPath,
                    new AgentMessage
                    {
                        Role = "user",
                        Content = "请总结当前文档。"
                    },
                    CancellationToken.None);

                var reloadedStore = new SqliteConversationStore(new SmartWordSqliteDatabase(databasePath));
                var history = await reloadedStore.GetHistoryAsync(documentPath, CancellationToken.None);

                Assert.Single(history);
                Assert.Equal("user", history[0].Role);
                Assert.Equal("请总结当前文档。", history[0].Content);
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }
        }

        [Fact]
        public async Task AppendToolResultAsync_WithRawInputAndOutput_PreservesToolMetadata()
        {
            var databasePath = CreateTempDatabasePath();
            try
            {
                var store = new SqliteConversationStore(new SmartWordSqliteDatabase(databasePath));

                await store.AppendToolResultAsync(
                    string.Empty,
                    "call-1",
                    "probe_document",
                    "{\"include_stats\":true}",
                    ToolCallResult.Ok("{\"paragraph_count\":10}"),
                    CancellationToken.None);

                var history = await store.GetHistoryAsync(string.Empty, CancellationToken.None);

                Assert.Single(history);
                Assert.Equal("tool", history[0].Role);
                Assert.Equal("call-1", history[0].ToolCallId);
                Assert.Equal("probe_document", history[0].ToolName);
                Assert.True(history[0].ToolSuccess);
                Assert.Contains("paragraph_count", history[0].Content);
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }
        }

        [Fact]
        public async Task GetHistoryAsync_DifferentDocumentPaths_ReturnsIsolatedHistories()
        {
            var databasePath = CreateTempDatabasePath();
            try
            {
                var store = new SqliteConversationStore(new SmartWordSqliteDatabase(databasePath));

                await store.AppendUserMessageAsync(
                    @"C:\docs\a.docx",
                    new AgentMessage { Role = "user", Content = "A 文档" },
                    CancellationToken.None);
                await store.AppendUserMessageAsync(
                    @"C:\docs\b.docx",
                    new AgentMessage { Role = "user", Content = "B 文档" },
                    CancellationToken.None);

                var firstHistory = await store.GetHistoryAsync(@"C:\docs\a.docx", CancellationToken.None);
                var secondHistory = await store.GetHistoryAsync(@"C:\docs\b.docx", CancellationToken.None);

                Assert.Single(firstHistory);
                Assert.Single(secondHistory);
                Assert.Equal("A 文档", firstHistory[0].Content);
                Assert.Equal("B 文档", secondHistory[0].Content);
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }
        }

        [Fact]
        public async Task CompleteRunAsync_Completed_UpdatesStatusAndCounts()
        {
            var databasePath = CreateTempDatabasePath();
            try
            {
                var store = new SqliteTaskHistoryStore(new SmartWordSqliteDatabase(databasePath));
                var run = await store.StartRunAsync(
                    new TaskRunStartRequest
                    {
                        DocumentPath = @"C:\docs\audit.docx",
                        UserGoal = "修正标题格式",
                        Mode = "agent",
                        PermissionMode = "confirm_writes",
                        Model = "gpt-4.1"
                    },
                    CancellationToken.None);

                await store.RecordToolAsync(
                    run.Id,
                    new TaskToolRecord
                    {
                        ToolCallId = "tool-1",
                        ToolName = "patch_range",
                        RawInput = "{\"description\":\"修正标题\"}",
                        Output = "{\"success\":true}",
                        Success = true
                    },
                    CancellationToken.None);
                await store.RecordChangeAsync(
                    run.Id,
                    new TaskChangeRecord
                    {
                        ToolCallId = "tool-1",
                        ToolName = "patch_range",
                        OperationDescription = "修正标题",
                        AffectedParagraphs = new[] { 3 },
                        Status = "executed",
                        Message = "已执行"
                    },
                    CancellationToken.None);
                await store.RecordChangeAsync(
                    run.Id,
                    new TaskChangeRecord
                    {
                        ToolCallId = "tool-1",
                        ToolName = "patch_range",
                        OperationDescription = "修正标题",
                        AffectedParagraphs = new[] { 3 },
                        Status = "verified",
                        Message = "已验证"
                    },
                    CancellationToken.None);

                await store.CompleteRunAsync(
                    run.Id,
                    new TaskRunCompletion
                    {
                        Status = TaskRunStatus.Completed,
                        Summary = "已完成任务。",
                        CompletedSteps = 1,
                        TotalSteps = 1
                    },
                    CancellationToken.None);

                var recentRuns = await store.GetRecentRunsAsync(@"C:\docs\audit.docx", 20, CancellationToken.None);

                Assert.Single(recentRuns);
                Assert.Equal(TaskRunStatus.Completed, recentRuns[0].Status);
                Assert.Equal(1, recentRuns[0].ToolCount);
                Assert.Equal(1, recentRuns[0].ChangeCount);
                Assert.Equal(1, recentRuns[0].VerifiedChangeCount);
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }
        }

        [Fact]
        public async Task GetRunDetailAsync_ExistingRun_ReturnsToolsAndChanges()
        {
            var databasePath = CreateTempDatabasePath();
            try
            {
                var store = new SqliteTaskHistoryStore(new SmartWordSqliteDatabase(databasePath));
                var run = await store.StartRunAsync(
                    new TaskRunStartRequest
                    {
                        DocumentPath = @"C:\docs\detail.docx",
                        UserGoal = "验证历史详情",
                        Mode = "ask"
                    },
                    CancellationToken.None);

                await store.RecordToolAsync(
                    run.Id,
                    new TaskToolRecord
                    {
                        ToolCallId = "tool-1",
                        ToolName = "probe_document",
                        Output = "ok",
                        Success = true
                    },
                    CancellationToken.None);
                await store.RecordChangeAsync(
                    run.Id,
                    new TaskChangeRecord
                    {
                        ToolCallId = "change-1",
                        ToolName = "patch_range",
                        AffectedParagraphs = new[] { 8, 9 },
                        Status = "verified"
                    },
                    CancellationToken.None);

                var detail = await store.GetRunDetailAsync(run.Id, CancellationToken.None);

                Assert.NotNull(detail);
                Assert.Equal(run.Id, detail.Run.Id);
                Assert.Single(detail.Tools);
                Assert.Single(detail.Changes);
                Assert.Equal(new[] { 8, 9 }, detail.Changes[0].AffectedParagraphs);
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }
        }

        [Fact]
        public async Task RecordToolAsync_WithApiKey_RedactsSecretBeforePersisting()
        {
            var databasePath = CreateTempDatabasePath();
            try
            {
                var store = new SqliteTaskHistoryStore(new SmartWordSqliteDatabase(databasePath));
                var run = await store.StartRunAsync(
                    new TaskRunStartRequest
                    {
                        DocumentPath = @"C:\docs\secret.docx",
                        UserGoal = "测试密钥脱敏",
                        Mode = "ask"
                    },
                    CancellationToken.None);

                await store.RecordToolAsync(
                    run.Id,
                    new TaskToolRecord
                    {
                        ToolCallId = "tool-1",
                        ToolName = "diagnostic",
                        RawInput = "Authorization: Bearer sk-testsecret1234567890",
                        Output = "api_key=sk-outputsecret1234567890",
                        Success = false
                    },
                    CancellationToken.None);

                var detail = await store.GetRunDetailAsync(run.Id, CancellationToken.None);

                Assert.DoesNotContain("sk-testsecret", detail.Tools[0].RawInput);
                Assert.DoesNotContain("sk-outputsecret", detail.Tools[0].Output);
                Assert.Contains("[REDACTED]", detail.Tools[0].RawInput);
                Assert.Contains("[REDACTED]", detail.Tools[0].Output);
            }
            finally
            {
                DeleteDatabaseFiles(databasePath);
            }
        }

        private static string CreateTempDatabasePath()
        {
            return Path.Combine(
                Path.GetTempPath(),
                "SmartWordTests",
                Guid.NewGuid().ToString("N"),
                "smartword-test.db");
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(directory, Path.GetFileName(databasePath) + "*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
