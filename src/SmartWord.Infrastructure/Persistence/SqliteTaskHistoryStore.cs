using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Infrastructure.Persistence
{
    /// <summary>
    /// 使用 SQLite 持久化用户可见的任务运行审计历史。
    /// </summary>
    public sealed class SqliteTaskHistoryStore : ITaskHistoryStore
    {
        private readonly SmartWordSqliteDatabase _database;

        public SqliteTaskHistoryStore()
            : this(new SmartWordSqliteDatabase())
        {
        }

        public SqliteTaskHistoryStore(SmartWordSqliteDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public Task<TaskRunRecord> StartRunAsync(
            TaskRunStartRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() =>
            {
                var safeRequest = request ?? new TaskRunStartRequest();
                var record = new TaskRunRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DocumentPath = NormalizeDocumentPath(safeRequest.DocumentPath),
                    DocumentKey = _database.CreateDocumentKey(safeRequest.DocumentPath),
                    UserGoal = SecretRedactor.Redact(safeRequest.UserGoal ?? string.Empty),
                    Mode = safeRequest.Mode ?? string.Empty,
                    PermissionMode = safeRequest.PermissionMode ?? string.Empty,
                    Model = safeRequest.Model ?? string.Empty,
                    Status = TaskRunStatus.Running,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedSteps = safeRequest.CompletedSteps,
                    TotalSteps = safeRequest.TotalSteps
                };

                using (var connection = _database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO task_runs(
    id, document_key, document_path, user_goal, mode, permission_mode, model,
    status, started_at_utc, completed_steps, total_steps)
VALUES(
    $id, $document_key, $document_path, $user_goal, $mode, $permission_mode, $model,
    $status, $started_at_utc, $completed_steps, $total_steps);";
                    command.Parameters.AddWithValue("$id", record.Id);
                    command.Parameters.AddWithValue("$document_key", record.DocumentKey);
                    command.Parameters.AddWithValue("$document_path", record.DocumentPath);
                    command.Parameters.AddWithValue("$user_goal", record.UserGoal);
                    command.Parameters.AddWithValue("$mode", record.Mode);
                    command.Parameters.AddWithValue("$permission_mode", record.PermissionMode);
                    command.Parameters.AddWithValue("$model", record.Model);
                    command.Parameters.AddWithValue("$status", record.Status.ToString());
                    command.Parameters.AddWithValue("$started_at_utc", record.StartedAtUtc.ToString("O"));
                    command.Parameters.AddWithValue("$completed_steps", record.CompletedSteps);
                    command.Parameters.AddWithValue("$total_steps", record.TotalSteps);
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }

                return record;
            }, cancellationToken);
        }

        public Task RecordToolAsync(
            string taskRunId,
            TaskToolRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(taskRunId) || record == null)
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                using (var connection = _database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO task_tools(
    task_run_id, tool_call_id, tool_name, operation_description,
    raw_input, output, success, created_at_utc)
VALUES(
    $task_run_id, $tool_call_id, $tool_name, $operation_description,
    $raw_input, $output, $success, $created_at_utc);";
                    command.Parameters.AddWithValue("$task_run_id", taskRunId);
                    command.Parameters.AddWithValue("$tool_call_id", record.ToolCallId ?? string.Empty);
                    command.Parameters.AddWithValue("$tool_name", record.ToolName ?? string.Empty);
                    command.Parameters.AddWithValue("$operation_description", record.OperationDescription ?? string.Empty);
                    command.Parameters.AddWithValue("$raw_input", SecretRedactor.Redact(record.RawInput ?? string.Empty));
                    command.Parameters.AddWithValue("$output", SecretRedactor.Redact(record.Output ?? string.Empty));
                    command.Parameters.AddWithValue("$success", record.Success ? 1 : 0);
                    command.Parameters.AddWithValue("$created_at_utc", EnsureUtc(record.CreatedAtUtc).ToString("O"));
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
            }, cancellationToken);
        }

        public Task RecordChangeAsync(
            string taskRunId,
            TaskChangeRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(taskRunId) || record == null)
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                using (var connection = _database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO task_changes(
    task_run_id, tool_call_id, tool_name, operation_description,
    affected_paragraphs_json, status, message, created_at_utc)
VALUES(
    $task_run_id, $tool_call_id, $tool_name, $operation_description,
    $affected_paragraphs_json, $status, $message, $created_at_utc);";
                    command.Parameters.AddWithValue("$task_run_id", taskRunId);
                    command.Parameters.AddWithValue("$tool_call_id", record.ToolCallId ?? string.Empty);
                    command.Parameters.AddWithValue("$tool_name", record.ToolName ?? string.Empty);
                    command.Parameters.AddWithValue("$operation_description", record.OperationDescription ?? string.Empty);
                    command.Parameters.AddWithValue(
                        "$affected_paragraphs_json",
                        JsonConvert.SerializeObject(record.AffectedParagraphs ?? new int[0]));
                    command.Parameters.AddWithValue("$status", record.Status ?? string.Empty);
                    command.Parameters.AddWithValue("$message", SecretRedactor.Redact(record.Message ?? string.Empty));
                    command.Parameters.AddWithValue("$created_at_utc", EnsureUtc(record.CreatedAtUtc).ToString("O"));
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
            }, cancellationToken);
        }

        public Task CompleteRunAsync(
            string taskRunId,
            TaskRunCompletion completion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(taskRunId))
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                var safeCompletion = completion ?? new TaskRunCompletion
                {
                    Status = TaskRunStatus.Failed,
                    FailureReason = "任务结束状态缺失。"
                };
                using (var connection = _database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var counts = AggregateCounts(connection, transaction, taskRunId);
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
UPDATE task_runs
SET status = $status,
    ended_at_utc = $ended_at_utc,
    summary = $summary,
    failure_reason = $failure_reason,
    cancel_reason = $cancel_reason,
    completed_steps = $completed_steps,
    total_steps = $total_steps,
    tool_count = $tool_count,
    change_count = $change_count,
    verified_change_count = $verified_change_count
WHERE id = $id;";
                        command.Parameters.AddWithValue("$id", taskRunId);
                        command.Parameters.AddWithValue("$status", safeCompletion.Status.ToString());
                        command.Parameters.AddWithValue("$ended_at_utc", EnsureUtc(safeCompletion.EndedAtUtc).ToString("O"));
                        command.Parameters.AddWithValue("$summary", SecretRedactor.Redact(safeCompletion.Summary ?? string.Empty));
                        command.Parameters.AddWithValue("$failure_reason", SecretRedactor.Redact(safeCompletion.FailureReason ?? string.Empty));
                        command.Parameters.AddWithValue("$cancel_reason", SecretRedactor.Redact(safeCompletion.CancelReason ?? string.Empty));
                        command.Parameters.AddWithValue("$completed_steps", safeCompletion.CompletedSteps);
                        command.Parameters.AddWithValue("$total_steps", safeCompletion.TotalSteps);
                        command.Parameters.AddWithValue("$tool_count", counts.ToolCount);
                        command.Parameters.AddWithValue("$change_count", counts.ChangeCount);
                        command.Parameters.AddWithValue("$verified_change_count", counts.VerifiedChangeCount);
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
            }, cancellationToken);
        }

        public Task<IReadOnlyList<TaskRunRecord>> GetRecentRunsAsync(
            string documentPath,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run<IReadOnlyList<TaskRunRecord>>(() =>
            {
                var safeLimit = Math.Max(1, Math.Min(50, limit <= 0 ? 20 : limit));
                var items = new List<TaskRunRecord>();
                using (var connection = _database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT *
FROM task_runs
WHERE document_key = $document_key
ORDER BY started_at_utc DESC
LIMIT $limit;";
                    command.Parameters.AddWithValue("$document_key", _database.CreateDocumentKey(documentPath));
                    command.Parameters.AddWithValue("$limit", safeLimit);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            items.Add(ReadRun(reader));
                        }
                    }
                }

                return items.AsReadOnly();
            }, cancellationToken);
        }

        public Task<TaskRunDetail> GetRunDetailAsync(
            string taskRunId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(taskRunId))
            {
                return Task.FromResult<TaskRunDetail>(null);
            }

            return Task.Run(() =>
            {
                TaskRunRecord run = null;
                var tools = new List<TaskToolRecord>();
                var changes = new List<TaskChangeRecord>();
                using (var connection = _database.OpenConnection())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT * FROM task_runs WHERE id = $id;";
                        command.Parameters.AddWithValue("$id", taskRunId);
                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                run = ReadRun(reader);
                            }
                        }
                    }

                    if (run == null)
                    {
                        return null;
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT tool_call_id, tool_name, operation_description, raw_input, output, success, created_at_utc
FROM task_tools
WHERE task_run_id = $task_run_id
ORDER BY id ASC;";
                        command.Parameters.AddWithValue("$task_run_id", taskRunId);
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                tools.Add(ReadTool(reader));
                            }
                        }
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
SELECT tool_call_id, tool_name, operation_description, affected_paragraphs_json, status, message, created_at_utc
FROM task_changes
WHERE task_run_id = $task_run_id
ORDER BY id ASC;";
                        command.Parameters.AddWithValue("$task_run_id", taskRunId);
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                changes.Add(ReadChange(reader));
                            }
                        }
                    }
                }

                return new TaskRunDetail
                {
                    Run = run,
                    Tools = tools.AsReadOnly(),
                    Changes = changes.AsReadOnly()
                };
            }, cancellationToken);
        }

        private static CountSnapshot AggregateCounts(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string taskRunId)
        {
            var toolCount = 0;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT COUNT(*) FROM task_tools WHERE task_run_id = $task_run_id;";
                command.Parameters.AddWithValue("$task_run_id", taskRunId);
                toolCount = Convert.ToInt32(command.ExecuteScalar());
            }

            var latestChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT tool_call_id, status
FROM task_changes
WHERE task_run_id = $task_run_id
ORDER BY id ASC;";
                command.Parameters.AddWithValue("$task_run_id", taskRunId);
                using (var reader = command.ExecuteReader())
                {
                    var fallbackIndex = 0;
                    while (reader.Read())
                    {
                        var key = ReadString(reader, "tool_call_id");
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            fallbackIndex++;
                            key = "__change_" + fallbackIndex;
                        }

                        latestChanges[key] = ReadString(reader, "status");
                    }
                }
            }

            return new CountSnapshot
            {
                ToolCount = toolCount,
                ChangeCount = latestChanges.Count,
                VerifiedChangeCount = latestChanges.Values.Count(item =>
                    string.Equals(item, "verified", StringComparison.OrdinalIgnoreCase))
            };
        }

        private static TaskRunRecord ReadRun(IDataRecord reader)
        {
            return new TaskRunRecord
            {
                Id = ReadString(reader, "id"),
                DocumentKey = ReadString(reader, "document_key"),
                DocumentPath = ReadString(reader, "document_path"),
                UserGoal = ReadString(reader, "user_goal"),
                Mode = ReadString(reader, "mode"),
                PermissionMode = ReadString(reader, "permission_mode"),
                Model = ReadString(reader, "model"),
                Status = ParseStatus(ReadString(reader, "status")),
                StartedAtUtc = ParseDate(ReadString(reader, "started_at_utc")),
                EndedAtUtc = ParseNullableDate(ReadString(reader, "ended_at_utc")),
                Summary = ReadString(reader, "summary"),
                FailureReason = ReadString(reader, "failure_reason"),
                CancelReason = ReadString(reader, "cancel_reason"),
                CompletedSteps = ReadInt(reader, "completed_steps"),
                TotalSteps = ReadInt(reader, "total_steps"),
                ToolCount = ReadInt(reader, "tool_count"),
                ChangeCount = ReadInt(reader, "change_count"),
                VerifiedChangeCount = ReadInt(reader, "verified_change_count")
            };
        }

        private static TaskToolRecord ReadTool(IDataRecord reader)
        {
            return new TaskToolRecord
            {
                ToolCallId = ReadString(reader, "tool_call_id"),
                ToolName = ReadString(reader, "tool_name"),
                OperationDescription = ReadString(reader, "operation_description"),
                RawInput = ReadString(reader, "raw_input"),
                Output = ReadString(reader, "output"),
                Success = ReadInt(reader, "success") == 1,
                CreatedAtUtc = ParseDate(ReadString(reader, "created_at_utc"))
            };
        }

        private static TaskChangeRecord ReadChange(IDataRecord reader)
        {
            var paragraphs = new int[0];
            var paragraphsJson = ReadString(reader, "affected_paragraphs_json");
            if (!string.IsNullOrWhiteSpace(paragraphsJson))
            {
                try
                {
                    paragraphs = JsonConvert.DeserializeObject<int[]>(paragraphsJson) ?? new int[0];
                }
                catch (JsonException)
                {
                    paragraphs = new int[0];
                }
            }

            return new TaskChangeRecord
            {
                ToolCallId = ReadString(reader, "tool_call_id"),
                ToolName = ReadString(reader, "tool_name"),
                OperationDescription = ReadString(reader, "operation_description"),
                AffectedParagraphs = paragraphs,
                Status = ReadString(reader, "status"),
                Message = ReadString(reader, "message"),
                CreatedAtUtc = ParseDate(ReadString(reader, "created_at_utc"))
            };
        }

        private static string NormalizeDocumentPath(string documentPath)
        {
            return string.IsNullOrWhiteSpace(documentPath) ? "__active_document__" : documentPath;
        }

        private static DateTimeOffset EnsureUtc(DateTimeOffset value)
        {
            return value == default(DateTimeOffset) ? DateTimeOffset.UtcNow : value.ToUniversalTime();
        }

        private static TaskRunStatus ParseStatus(string value)
        {
            return Enum.TryParse<TaskRunStatus>(value, true, out var status)
                ? status
                : TaskRunStatus.Running;
        }

        private static DateTimeOffset ParseDate(string value)
        {
            return DateTimeOffset.TryParse(value, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTimeOffset.MinValue;
        }

        private static DateTimeOffset? ParseNullableDate(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (DateTimeOffset?)null : ParseDate(value);
        }

        private static string ReadString(IDataRecord reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? string.Empty : Convert.ToString(reader.GetValue(ordinal));
        }

        private static int ReadInt(IDataRecord reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
        }

        private sealed class CountSnapshot
        {
            public int ToolCount { get; set; }

            public int ChangeCount { get; set; }

            public int VerifiedChangeCount { get; set; }
        }
    }
}
