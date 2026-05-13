using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SQLitePCL;
using SmartWord.Core.Telemetry;

namespace SmartWord.Infrastructure.Telemetry
{
    /// <summary>
    /// 写入独立评测 SQLite 数据库，避免污染用户正式 smartword.db。
    /// </summary>
    public sealed class SqliteEvalTelemetrySink : IAgentTelemetrySink
    {
        private static readonly object InitializeSyncRoot = new object();
        private static bool _sqliteInitialized;
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly string _databasePath;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private bool _schemaInitialized;

        public SqliteEvalTelemetrySink(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException("eval.sqlite 路径不能为空。", nameof(databasePath));
            }

            _databasePath = databasePath;
        }

        public async Task RecordAsync(AgentTelemetryEvent telemetryEvent, CancellationToken cancellationToken)
        {
            if (telemetryEvent == null)
            {
                return;
            }

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureSchema();
                using (var connection = CreateOpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    InsertRawEvent(connection, transaction, telemetryEvent);
                    InsertProjectedEvent(connection, transaction, telemetryEvent);
                    transaction.Commit();
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private void EnsureSchema()
        {
            lock (InitializeSyncRoot)
            {
                if (_schemaInitialized)
                {
                    return;
                }

                EnsureSqliteProviderInitialized();
                var directory = Path.GetDirectoryName(_databasePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var connection = CreateOpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS eval_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id TEXT NOT NULL,
    event_type TEXT NOT NULL,
    eval_run_id TEXT,
    task_run_id TEXT,
    case_id TEXT,
    level TEXT,
    variant TEXT,
    mode TEXT,
    permission_mode TEXT,
    model TEXT,
    timestamp_utc TEXT NOT NULL,
    payload_json TEXT NOT NULL
);");
                    ExecuteNonQuery(connection, transaction, @"
CREATE INDEX IF NOT EXISTS ix_eval_events_run_case
ON eval_events(eval_run_id, case_id, event_type);");
                    ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS eval_runs (
    eval_run_id TEXT PRIMARY KEY,
    variant TEXT,
    model TEXT,
    started_at_utc TEXT,
    completed_at_utc TEXT,
    output_dir TEXT,
    status TEXT
);");
                    ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS eval_tasks (
    task_run_id TEXT PRIMARY KEY,
    eval_run_id TEXT,
    case_id TEXT,
    level TEXT,
    variant TEXT,
    mode TEXT,
    permission_mode TEXT,
    model TEXT,
    input_docx TEXT,
    output_docx TEXT,
    started_at_utc TEXT,
    completed_at_utc TEXT,
    duration_ms INTEGER,
    status TEXT,
    failure_type TEXT,
    failure_reason TEXT
);");
                    ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS eval_llm_calls (
    llm_call_id TEXT PRIMARY KEY,
    task_run_id TEXT,
    eval_run_id TEXT,
    case_id TEXT,
    model TEXT,
    temperature REAL,
    message_count INTEGER,
    tool_schema_count INTEGER,
    estimated_prompt_tokens INTEGER,
    estimated_completion_tokens INTEGER,
    prompt_tokens INTEGER,
    completion_tokens INTEGER,
    total_tokens INTEGER,
    duration_ms INTEGER,
    finish_reason TEXT,
    tool_call_count INTEGER,
    success INTEGER,
    failure_type TEXT,
    error_message TEXT,
    started_at_utc TEXT,
    completed_at_utc TEXT
);");
                    ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS eval_tool_calls (
    tool_call_id TEXT,
    task_run_id TEXT,
    eval_run_id TEXT,
    case_id TEXT,
    llm_call_id TEXT,
    tool_name TEXT,
    raw_input TEXT,
    operation_description TEXT,
    started_at_utc TEXT,
    completed_at_utc TEXT,
    duration_ms INTEGER,
    success INTEGER,
    failure_type TEXT,
    error_message TEXT,
    affected_paragraphs TEXT,
    paragraph_refs TEXT,
    output_size_chars INTEGER,
    requires_confirmation INTEGER,
    was_confirmed INTEGER,
    is_safety_block INTEGER,
    is_relevant INTEGER,
    is_accurate INTEGER,
    accuracy_reason TEXT,
    PRIMARY KEY(task_run_id, tool_call_id)
);");
                    ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS eval_confirmations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_run_id TEXT,
    eval_run_id TEXT,
    case_id TEXT,
    tool_call_id TEXT,
    tool_name TEXT,
    event_type TEXT,
    requested_at_utc TEXT,
    decided_at_utc TEXT,
    duration_ms INTEGER,
    confirmed INTEGER,
    remember INTEGER,
    policy TEXT,
    reason TEXT
);");
                    ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS eval_verifications (
    verification_id TEXT PRIMARY KEY,
    task_run_id TEXT,
    eval_run_id TEXT,
    case_id TEXT,
    tool_call_id TEXT,
    duration_ms INTEGER,
    success INTEGER,
    checks_json TEXT,
    failure_reason TEXT,
    started_at_utc TEXT,
    completed_at_utc TEXT
);");
                    ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS eval_context_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_run_id TEXT,
    eval_run_id TEXT,
    case_id TEXT,
    before_tokens INTEGER,
    after_tokens INTEGER,
    tokens_saved INTEGER,
    message_count_before INTEGER,
    message_count_after INTEGER,
    strategy TEXT,
    was_compacted INTEGER,
    created_at_utc TEXT
);");
                    ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS eval_scores (
    task_run_id TEXT PRIMARY KEY,
    eval_run_id TEXT,
    case_id TEXT,
    score REAL,
    passed INTEGER,
    strict_pass INTEGER,
    safety_violation INTEGER,
    checks_json TEXT,
    scored_at_utc TEXT
);");
                    transaction.Commit();
                }

                _schemaInitialized = true;
            }
        }

        private SqliteConnection CreateOpenConnection()
        {
            var connection = new SqliteConnection("Data Source=" + _databasePath + ";Cache=Shared");
            connection.Open();
            ExecuteNonQuery(connection, null, "PRAGMA foreign_keys = ON;");
            ExecuteNonQuery(connection, null, "PRAGMA busy_timeout = 5000;");
            ExecuteNonQuery(connection, null, "PRAGMA journal_mode = WAL;");
            return connection;
        }

        private static void InsertRawEvent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            AgentTelemetryEvent telemetryEvent)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO eval_events(
    event_id, event_type, eval_run_id, task_run_id, case_id, level, variant,
    mode, permission_mode, model, timestamp_utc, payload_json)
VALUES (
    $event_id, $event_type, $eval_run_id, $task_run_id, $case_id, $level, $variant,
    $mode, $permission_mode, $model, $timestamp_utc, $payload_json);";
                command.Parameters.AddWithValue("$event_id", telemetryEvent.EventId ?? string.Empty);
                command.Parameters.AddWithValue("$event_type", telemetryEvent.EventType ?? string.Empty);
                command.Parameters.AddWithValue("$eval_run_id", telemetryEvent.EvalRunId ?? string.Empty);
                command.Parameters.AddWithValue("$task_run_id", telemetryEvent.TaskRunId ?? string.Empty);
                command.Parameters.AddWithValue("$case_id", telemetryEvent.CaseId ?? string.Empty);
                command.Parameters.AddWithValue("$level", telemetryEvent.Level ?? string.Empty);
                command.Parameters.AddWithValue("$variant", telemetryEvent.Variant ?? string.Empty);
                command.Parameters.AddWithValue("$mode", telemetryEvent.Mode ?? string.Empty);
                command.Parameters.AddWithValue("$permission_mode", telemetryEvent.PermissionMode ?? string.Empty);
                command.Parameters.AddWithValue("$model", telemetryEvent.Model ?? string.Empty);
                command.Parameters.AddWithValue("$timestamp_utc", telemetryEvent.TimestampUtc.ToString("O"));
                command.Parameters.AddWithValue("$payload_json", JsonConvert.SerializeObject(telemetryEvent, SerializerSettings));
                command.ExecuteNonQuery();
            }
        }

        private static void InsertProjectedEvent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            AgentTelemetryEvent telemetryEvent)
        {
            var data = JObject.FromObject(telemetryEvent.Data ?? new object(), JsonSerializer.Create(SerializerSettings));
            switch ((telemetryEvent.EventType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "eval_run_started":
                    UpsertEvalRun(connection, transaction, telemetryEvent, data, false);
                    break;
                case "eval_run_completed":
                case "eval_run_failed":
                    UpsertEvalRun(connection, transaction, telemetryEvent, data, true);
                    break;
                case "task_started":
                    UpsertTask(connection, transaction, telemetryEvent, data, false);
                    break;
                case "task_completed":
                case "task_failed":
                case "task_cancelled":
                    UpsertTask(connection, transaction, telemetryEvent, data, true);
                    break;
                case "llm_call_completed":
                case "llm_call_failed":
                    UpsertLlmCall(connection, transaction, telemetryEvent, data);
                    break;
                case "tool_call_completed":
                case "tool_call_failed":
                case "tool_call_denied":
                case "tool_call_skipped":
                    UpsertToolCall(connection, transaction, telemetryEvent, data);
                    break;
                case "confirmation_requested":
                case "confirmation_decided":
                    InsertConfirmation(connection, transaction, telemetryEvent, data);
                    break;
                case "verification_completed":
                case "verification_failed":
                    UpsertVerification(connection, transaction, telemetryEvent, data);
                    break;
                case "context_compressed":
                    InsertContextEvent(connection, transaction, telemetryEvent, data);
                    break;
                case "score_completed":
                    UpsertScore(connection, transaction, telemetryEvent, data);
                    break;
            }
        }

        private static void UpsertEvalRun(SqliteConnection connection, SqliteTransaction transaction, AgentTelemetryEvent e, JObject data, bool completed)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO eval_runs(eval_run_id, variant, model, started_at_utc, completed_at_utc, output_dir, status)
VALUES($eval_run_id, $variant, $model, $started_at_utc, $completed_at_utc, $output_dir, $status)
ON CONFLICT(eval_run_id) DO UPDATE SET
    completed_at_utc = COALESCE(excluded.completed_at_utc, eval_runs.completed_at_utc),
    status = excluded.status,
    output_dir = COALESCE(NULLIF(excluded.output_dir, ''), eval_runs.output_dir);";
                command.Parameters.AddWithValue("$eval_run_id", e.EvalRunId ?? string.Empty);
                command.Parameters.AddWithValue("$variant", e.Variant ?? string.Empty);
                command.Parameters.AddWithValue("$model", e.Model ?? string.Empty);
                command.Parameters.AddWithValue("$started_at_utc", completed ? data.Value<string>("startedAtUtc") ?? string.Empty : e.TimestampUtc.ToString("O"));
                command.Parameters.AddWithValue("$completed_at_utc", completed ? e.TimestampUtc.ToString("O") : string.Empty);
                command.Parameters.AddWithValue("$output_dir", data.Value<string>("outputDir") ?? string.Empty);
                command.Parameters.AddWithValue("$status", data.Value<string>("status") ?? (completed ? "completed" : "running"));
                command.ExecuteNonQuery();
            }
        }

        private static void UpsertTask(SqliteConnection connection, SqliteTransaction transaction, AgentTelemetryEvent e, JObject data, bool completed)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO eval_tasks(
    task_run_id, eval_run_id, case_id, level, variant, mode, permission_mode, model,
    input_docx, output_docx, started_at_utc, completed_at_utc, duration_ms,
    status, failure_type, failure_reason)
VALUES(
    $task_run_id, $eval_run_id, $case_id, $level, $variant, $mode, $permission_mode, $model,
    $input_docx, $output_docx, $started_at_utc, $completed_at_utc, $duration_ms,
    $status, $failure_type, $failure_reason)
ON CONFLICT(task_run_id) DO UPDATE SET
    output_docx = COALESCE(NULLIF(excluded.output_docx, ''), eval_tasks.output_docx),
    completed_at_utc = COALESCE(NULLIF(excluded.completed_at_utc, ''), eval_tasks.completed_at_utc),
    duration_ms = COALESCE(excluded.duration_ms, eval_tasks.duration_ms),
    status = excluded.status,
    failure_type = excluded.failure_type,
    failure_reason = excluded.failure_reason;";
                command.Parameters.AddWithValue("$task_run_id", e.TaskRunId ?? string.Empty);
                command.Parameters.AddWithValue("$eval_run_id", e.EvalRunId ?? string.Empty);
                command.Parameters.AddWithValue("$case_id", e.CaseId ?? string.Empty);
                command.Parameters.AddWithValue("$level", e.Level ?? string.Empty);
                command.Parameters.AddWithValue("$variant", e.Variant ?? string.Empty);
                command.Parameters.AddWithValue("$mode", e.Mode ?? string.Empty);
                command.Parameters.AddWithValue("$permission_mode", e.PermissionMode ?? string.Empty);
                command.Parameters.AddWithValue("$model", e.Model ?? string.Empty);
                command.Parameters.AddWithValue("$input_docx", data.Value<string>("inputDocx") ?? string.Empty);
                command.Parameters.AddWithValue("$output_docx", data.Value<string>("outputDocx") ?? string.Empty);
                command.Parameters.AddWithValue("$started_at_utc", data.Value<string>("startedAtUtc") ?? (completed ? string.Empty : e.TimestampUtc.ToString("O")));
                command.Parameters.AddWithValue("$completed_at_utc", completed ? e.TimestampUtc.ToString("O") : string.Empty);
                command.Parameters.AddWithValue("$duration_ms", ToDb(data.Value<long?>("durationMs")));
                command.Parameters.AddWithValue("$status", data.Value<string>("status") ?? (completed ? "completed" : "running"));
                command.Parameters.AddWithValue("$failure_type", data.Value<string>("failureType") ?? string.Empty);
                command.Parameters.AddWithValue("$failure_reason", data.Value<string>("failureReason") ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        private static void UpsertLlmCall(SqliteConnection connection, SqliteTransaction transaction, AgentTelemetryEvent e, JObject data)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO eval_llm_calls(
    llm_call_id, task_run_id, eval_run_id, case_id, model, temperature,
    message_count, tool_schema_count, estimated_prompt_tokens, estimated_completion_tokens,
    prompt_tokens, completion_tokens, total_tokens, duration_ms, finish_reason,
    tool_call_count, success, failure_type, error_message, started_at_utc, completed_at_utc)
VALUES(
    $llm_call_id, $task_run_id, $eval_run_id, $case_id, $model, $temperature,
    $message_count, $tool_schema_count, $estimated_prompt_tokens, $estimated_completion_tokens,
    $prompt_tokens, $completion_tokens, $total_tokens, $duration_ms, $finish_reason,
    $tool_call_count, $success, $failure_type, $error_message, $started_at_utc, $completed_at_utc);";
                command.Parameters.AddWithValue("$llm_call_id", data.Value<string>("llmCallId") ?? string.Empty);
                command.Parameters.AddWithValue("$task_run_id", e.TaskRunId ?? string.Empty);
                command.Parameters.AddWithValue("$eval_run_id", e.EvalRunId ?? string.Empty);
                command.Parameters.AddWithValue("$case_id", e.CaseId ?? string.Empty);
                command.Parameters.AddWithValue("$model", data.Value<string>("model") ?? e.Model ?? string.Empty);
                command.Parameters.AddWithValue("$temperature", ToDb(data.Value<double?>("temperature")));
                command.Parameters.AddWithValue("$message_count", ToDb(data.Value<int?>("messageCount")));
                command.Parameters.AddWithValue("$tool_schema_count", ToDb(data.Value<int?>("toolSchemaCount")));
                command.Parameters.AddWithValue("$estimated_prompt_tokens", ToDb(data.Value<int?>("estimatedPromptTokens")));
                command.Parameters.AddWithValue("$estimated_completion_tokens", ToDb(data.Value<int?>("estimatedCompletionTokens")));
                command.Parameters.AddWithValue("$prompt_tokens", ToDb(data.Value<int?>("promptTokens")));
                command.Parameters.AddWithValue("$completion_tokens", ToDb(data.Value<int?>("completionTokens")));
                command.Parameters.AddWithValue("$total_tokens", ToDb(data.Value<int?>("totalTokens")));
                command.Parameters.AddWithValue("$duration_ms", ToDb(data.Value<long?>("durationMs")));
                command.Parameters.AddWithValue("$finish_reason", data.Value<string>("finishReason") ?? string.Empty);
                command.Parameters.AddWithValue("$tool_call_count", ToDb(data.Value<int?>("toolCallCount")));
                command.Parameters.AddWithValue("$success", data.Value<bool?>("success") == true ? 1 : 0);
                command.Parameters.AddWithValue("$failure_type", data.Value<string>("failureType") ?? string.Empty);
                command.Parameters.AddWithValue("$error_message", data.Value<string>("errorMessage") ?? string.Empty);
                command.Parameters.AddWithValue("$started_at_utc", data.Value<string>("startedAtUtc") ?? string.Empty);
                command.Parameters.AddWithValue("$completed_at_utc", data.Value<string>("completedAtUtc") ?? e.TimestampUtc.ToString("O"));
                command.ExecuteNonQuery();
            }
        }

        private static void UpsertToolCall(SqliteConnection connection, SqliteTransaction transaction, AgentTelemetryEvent e, JObject data)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO eval_tool_calls(
    tool_call_id, task_run_id, eval_run_id, case_id, llm_call_id, tool_name, raw_input,
    operation_description, started_at_utc, completed_at_utc, duration_ms, success,
    failure_type, error_message, affected_paragraphs, paragraph_refs, output_size_chars,
    requires_confirmation, was_confirmed, is_safety_block, is_relevant, is_accurate, accuracy_reason)
VALUES(
    $tool_call_id, $task_run_id, $eval_run_id, $case_id, $llm_call_id, $tool_name, $raw_input,
    $operation_description, $started_at_utc, $completed_at_utc, $duration_ms, $success,
    $failure_type, $error_message, $affected_paragraphs, $paragraph_refs, $output_size_chars,
    $requires_confirmation, $was_confirmed, $is_safety_block, $is_relevant, $is_accurate, $accuracy_reason);";
                command.Parameters.AddWithValue("$tool_call_id", data.Value<string>("toolCallId") ?? string.Empty);
                command.Parameters.AddWithValue("$task_run_id", e.TaskRunId ?? string.Empty);
                command.Parameters.AddWithValue("$eval_run_id", e.EvalRunId ?? string.Empty);
                command.Parameters.AddWithValue("$case_id", e.CaseId ?? string.Empty);
                command.Parameters.AddWithValue("$llm_call_id", data.Value<string>("llmCallId") ?? string.Empty);
                command.Parameters.AddWithValue("$tool_name", data.Value<string>("toolName") ?? e.DataValue("toolName"));
                command.Parameters.AddWithValue("$raw_input", data.Value<string>("rawInput") ?? string.Empty);
                command.Parameters.AddWithValue("$operation_description", data.Value<string>("operationDescription") ?? string.Empty);
                command.Parameters.AddWithValue("$started_at_utc", data.Value<string>("startedAtUtc") ?? string.Empty);
                command.Parameters.AddWithValue("$completed_at_utc", data.Value<string>("completedAtUtc") ?? e.TimestampUtc.ToString("O"));
                command.Parameters.AddWithValue("$duration_ms", ToDb(data.Value<long?>("durationMs")));
                command.Parameters.AddWithValue("$success", data.Value<bool?>("success") == true ? 1 : 0);
                command.Parameters.AddWithValue("$failure_type", data.Value<string>("failureType") ?? string.Empty);
                command.Parameters.AddWithValue("$error_message", data.Value<string>("errorMessage") ?? string.Empty);
                command.Parameters.AddWithValue("$affected_paragraphs", data["affectedParagraphs"] == null ? string.Empty : data["affectedParagraphs"].ToString(Formatting.None));
                command.Parameters.AddWithValue("$paragraph_refs", data["paragraphRefs"] == null ? string.Empty : data["paragraphRefs"].ToString(Formatting.None));
                command.Parameters.AddWithValue("$output_size_chars", ToDb(data.Value<int?>("outputSizeChars")));
                command.Parameters.AddWithValue("$requires_confirmation", data.Value<bool?>("requiresConfirmation") == true ? 1 : 0);
                command.Parameters.AddWithValue("$was_confirmed", data.Value<bool?>("wasConfirmed") == true ? 1 : 0);
                command.Parameters.AddWithValue("$is_safety_block", data.Value<bool?>("isSafetyBlock") == true ? 1 : 0);
                command.Parameters.AddWithValue("$is_relevant", ToDbBool(data.Value<bool?>("isRelevant")));
                command.Parameters.AddWithValue("$is_accurate", ToDbBool(data.Value<bool?>("isAccurate")));
                command.Parameters.AddWithValue("$accuracy_reason", data.Value<string>("accuracyReason") ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        private static void InsertConfirmation(SqliteConnection connection, SqliteTransaction transaction, AgentTelemetryEvent e, JObject data)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO eval_confirmations(
    task_run_id, eval_run_id, case_id, tool_call_id, tool_name, event_type,
    requested_at_utc, decided_at_utc, duration_ms, confirmed, remember, policy, reason)
VALUES(
    $task_run_id, $eval_run_id, $case_id, $tool_call_id, $tool_name, $event_type,
    $requested_at_utc, $decided_at_utc, $duration_ms, $confirmed, $remember, $policy, $reason);";
                command.Parameters.AddWithValue("$task_run_id", e.TaskRunId ?? string.Empty);
                command.Parameters.AddWithValue("$eval_run_id", e.EvalRunId ?? string.Empty);
                command.Parameters.AddWithValue("$case_id", e.CaseId ?? string.Empty);
                command.Parameters.AddWithValue("$tool_call_id", data.Value<string>("toolCallId") ?? string.Empty);
                command.Parameters.AddWithValue("$tool_name", data.Value<string>("toolName") ?? string.Empty);
                command.Parameters.AddWithValue("$event_type", e.EventType ?? string.Empty);
                command.Parameters.AddWithValue("$requested_at_utc", data.Value<string>("requestedAtUtc") ?? e.TimestampUtc.ToString("O"));
                command.Parameters.AddWithValue("$decided_at_utc", data.Value<string>("decidedAtUtc") ?? string.Empty);
                command.Parameters.AddWithValue("$duration_ms", ToDb(data.Value<long?>("durationMs")));
                command.Parameters.AddWithValue("$confirmed", ToDbBool(data.Value<bool?>("confirmed")));
                command.Parameters.AddWithValue("$remember", ToDbBool(data.Value<bool?>("remember")));
                command.Parameters.AddWithValue("$policy", data.Value<string>("policy") ?? string.Empty);
                command.Parameters.AddWithValue("$reason", data.Value<string>("reason") ?? string.Empty);
                command.ExecuteNonQuery();
            }
        }

        private static void UpsertVerification(SqliteConnection connection, SqliteTransaction transaction, AgentTelemetryEvent e, JObject data)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO eval_verifications(
    verification_id, task_run_id, eval_run_id, case_id, tool_call_id, duration_ms,
    success, checks_json, failure_reason, started_at_utc, completed_at_utc)
VALUES(
    $verification_id, $task_run_id, $eval_run_id, $case_id, $tool_call_id, $duration_ms,
    $success, $checks_json, $failure_reason, $started_at_utc, $completed_at_utc);";
                command.Parameters.AddWithValue("$verification_id", data.Value<string>("verificationId") ?? Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$task_run_id", e.TaskRunId ?? string.Empty);
                command.Parameters.AddWithValue("$eval_run_id", e.EvalRunId ?? string.Empty);
                command.Parameters.AddWithValue("$case_id", e.CaseId ?? string.Empty);
                command.Parameters.AddWithValue("$tool_call_id", data.Value<string>("toolCallId") ?? string.Empty);
                command.Parameters.AddWithValue("$duration_ms", ToDb(data.Value<long?>("durationMs")));
                command.Parameters.AddWithValue("$success", data.Value<bool?>("success") == true ? 1 : 0);
                command.Parameters.AddWithValue("$checks_json", data["checksJson"] == null ? string.Empty : data["checksJson"].ToString(Formatting.None));
                command.Parameters.AddWithValue("$failure_reason", data.Value<string>("failureReason") ?? string.Empty);
                command.Parameters.AddWithValue("$started_at_utc", data.Value<string>("startedAtUtc") ?? string.Empty);
                command.Parameters.AddWithValue("$completed_at_utc", data.Value<string>("completedAtUtc") ?? e.TimestampUtc.ToString("O"));
                command.ExecuteNonQuery();
            }
        }

        private static void InsertContextEvent(SqliteConnection connection, SqliteTransaction transaction, AgentTelemetryEvent e, JObject data)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO eval_context_events(
    task_run_id, eval_run_id, case_id, before_tokens, after_tokens, tokens_saved,
    message_count_before, message_count_after, strategy, was_compacted, created_at_utc)
VALUES(
    $task_run_id, $eval_run_id, $case_id, $before_tokens, $after_tokens, $tokens_saved,
    $message_count_before, $message_count_after, $strategy, $was_compacted, $created_at_utc);";
                command.Parameters.AddWithValue("$task_run_id", e.TaskRunId ?? string.Empty);
                command.Parameters.AddWithValue("$eval_run_id", e.EvalRunId ?? string.Empty);
                command.Parameters.AddWithValue("$case_id", e.CaseId ?? string.Empty);
                command.Parameters.AddWithValue("$before_tokens", ToDb(data.Value<int?>("beforeTokens")));
                command.Parameters.AddWithValue("$after_tokens", ToDb(data.Value<int?>("afterTokens")));
                command.Parameters.AddWithValue("$tokens_saved", ToDb(data.Value<int?>("tokensSaved")));
                command.Parameters.AddWithValue("$message_count_before", ToDb(data.Value<int?>("messageCountBefore")));
                command.Parameters.AddWithValue("$message_count_after", ToDb(data.Value<int?>("messageCountAfter")));
                command.Parameters.AddWithValue("$strategy", data.Value<string>("strategy") ?? string.Empty);
                command.Parameters.AddWithValue("$was_compacted", data.Value<bool?>("wasCompacted") == true ? 1 : 0);
                command.Parameters.AddWithValue("$created_at_utc", e.TimestampUtc.ToString("O"));
                command.ExecuteNonQuery();
            }
        }

        private static void UpsertScore(SqliteConnection connection, SqliteTransaction transaction, AgentTelemetryEvent e, JObject data)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR REPLACE INTO eval_scores(
    task_run_id, eval_run_id, case_id, score, passed, strict_pass,
    safety_violation, checks_json, scored_at_utc)
VALUES(
    $task_run_id, $eval_run_id, $case_id, $score, $passed, $strict_pass,
    $safety_violation, $checks_json, $scored_at_utc);";
                command.Parameters.AddWithValue("$task_run_id", e.TaskRunId ?? data.Value<string>("taskRunId") ?? string.Empty);
                command.Parameters.AddWithValue("$eval_run_id", e.EvalRunId ?? string.Empty);
                command.Parameters.AddWithValue("$case_id", e.CaseId ?? string.Empty);
                command.Parameters.AddWithValue("$score", ToDb(data.Value<double?>("score")));
                command.Parameters.AddWithValue("$passed", data.Value<bool?>("pass") == true ? 1 : 0);
                command.Parameters.AddWithValue("$strict_pass", data.Value<bool?>("strictPass") == true ? 1 : 0);
                command.Parameters.AddWithValue("$safety_violation", data.Value<bool?>("safetyViolation") == true ? 1 : 0);
                command.Parameters.AddWithValue("$checks_json", data["checks"] == null ? string.Empty : data["checks"].ToString(Formatting.None));
                command.Parameters.AddWithValue("$scored_at_utc", e.TimestampUtc.ToString("O"));
                command.ExecuteNonQuery();
            }
        }

        private static void EnsureSqliteProviderInitialized()
        {
            lock (InitializeSyncRoot)
            {
                if (_sqliteInitialized)
                {
                    return;
                }

                Batteries_V2.Init();
                _sqliteInitialized = true;
            }
        }

        private static object ToDb<T>(T? value)
            where T : struct
        {
            return value.HasValue ? (object)value.Value : DBNull.Value;
        }

        private static object ToDbBool(bool? value)
        {
            return value.HasValue ? (object)(value.Value ? 1 : 0) : DBNull.Value;
        }

        private static void ExecuteNonQuery(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string commandText)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = commandText;
                command.ExecuteNonQuery();
            }
        }
    }

    internal static class JObjectTelemetryExtensions
    {
        public static string DataValue(this AgentTelemetryEvent telemetryEvent, string key)
        {
            if (telemetryEvent == null || telemetryEvent.Data == null || !telemetryEvent.Data.TryGetValue(key, out var value))
            {
                return string.Empty;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }
}
