using System;
using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace SmartWord.Infrastructure.Persistence
{
    /// <summary>
    /// 负责 SmartWord 本地 SQLite 数据库路径、连接和幂等 schema 初始化。
    /// </summary>
    public sealed class SmartWordSqliteDatabase
    {
        private const int CurrentSchemaVersion = 1;
        private static readonly object InitializeSyncRoot = new object();
        private static bool _sqliteInitialized;
        private bool _schemaInitialized;

        public SmartWordSqliteDatabase()
            : this(GetDefaultDatabasePath())
        {
        }

        public SmartWordSqliteDatabase(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
            }

            DatabasePath = databasePath;
        }

        public string DatabasePath { get; }

        public SqliteConnection OpenConnection()
        {
            EnsureSchema();
            var connection = CreateConnection();
            connection.Open();
            ConfigureConnection(connection);
            return connection;
        }

        public string CreateDocumentKey(string documentPath)
        {
            var normalized = string.IsNullOrWhiteSpace(documentPath)
                ? "__active_document__"
                : documentPath.Trim();
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var item in bytes)
                {
                    builder.Append(item.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        public static string GetDefaultDatabasePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SmartWord",
                "smartword.db");
        }

        private SqliteConnection CreateConnection()
        {
            return new SqliteConnection("Data Source=" + DatabasePath + ";Cache=Shared");
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
                var directory = Path.GetDirectoryName(DatabasePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var connection = CreateConnection())
                {
                    connection.Open();
                    ConfigureConnection(connection);
                    using (var transaction = connection.BeginTransaction())
                    {
                        ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_at_utc TEXT NOT NULL
);");
                        ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS conversation_messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    document_key TEXT NOT NULL,
    document_path TEXT NOT NULL,
    role TEXT NOT NULL,
    content TEXT,
    reasoning_content TEXT,
    tool_call_id TEXT,
    name TEXT,
    tool_name TEXT,
    raw_tool_input TEXT,
    tool_success INTEGER NOT NULL DEFAULT 0,
    tool_calls_json TEXT,
    is_compressed_summary INTEGER NOT NULL DEFAULT 0,
    is_internal_observation INTEGER NOT NULL DEFAULT 0,
    internal_observation_kind TEXT,
    created_at_utc TEXT NOT NULL
);");
                        ExecuteNonQuery(connection, transaction, @"
CREATE INDEX IF NOT EXISTS ix_conversation_messages_document_id
ON conversation_messages(document_key, id);");
                        ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS task_runs (
    id TEXT PRIMARY KEY,
    document_key TEXT NOT NULL,
    document_path TEXT NOT NULL,
    user_goal TEXT NOT NULL,
    mode TEXT NOT NULL,
    permission_mode TEXT,
    model TEXT,
    status TEXT NOT NULL,
    started_at_utc TEXT NOT NULL,
    ended_at_utc TEXT,
    summary TEXT,
    failure_reason TEXT,
    cancel_reason TEXT,
    completed_steps INTEGER NOT NULL DEFAULT 0,
    total_steps INTEGER NOT NULL DEFAULT 0,
    tool_count INTEGER NOT NULL DEFAULT 0,
    change_count INTEGER NOT NULL DEFAULT 0,
    verified_change_count INTEGER NOT NULL DEFAULT 0
);");
                        ExecuteNonQuery(connection, transaction, @"
CREATE INDEX IF NOT EXISTS ix_task_runs_document_started
ON task_runs(document_key, started_at_utc DESC);");
                        ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS task_tools (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_run_id TEXT NOT NULL,
    tool_call_id TEXT,
    tool_name TEXT NOT NULL,
    operation_description TEXT,
    raw_input TEXT,
    output TEXT,
    success INTEGER NOT NULL DEFAULT 0,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY(task_run_id) REFERENCES task_runs(id) ON DELETE CASCADE
);");
                        ExecuteNonQuery(connection, transaction, @"
CREATE INDEX IF NOT EXISTS ix_task_tools_run_id
ON task_tools(task_run_id, id);");
                        ExecuteNonQuery(connection, transaction, @"
CREATE TABLE IF NOT EXISTS task_changes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    task_run_id TEXT NOT NULL,
    tool_call_id TEXT,
    tool_name TEXT,
    operation_description TEXT,
    affected_paragraphs_json TEXT,
    status TEXT NOT NULL,
    message TEXT,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY(task_run_id) REFERENCES task_runs(id) ON DELETE CASCADE
);");
                        ExecuteNonQuery(connection, transaction, @"
CREATE INDEX IF NOT EXISTS ix_task_changes_run_id
ON task_changes(task_run_id, id);");

                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
INSERT OR IGNORE INTO schema_migrations(version, applied_at_utc)
VALUES ($version, $applied_at_utc);";
                            command.Parameters.AddWithValue("$version", CurrentSchemaVersion);
                            command.Parameters.AddWithValue("$applied_at_utc", DateTimeOffset.UtcNow.ToString("O"));
                            command.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                }

                _schemaInitialized = true;
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

        private static void ConfigureConnection(SqliteConnection connection)
        {
            if (connection == null || connection.State != ConnectionState.Open)
            {
                return;
            }

            ExecuteNonQuery(connection, null, "PRAGMA foreign_keys = ON;");
            ExecuteNonQuery(connection, null, "PRAGMA busy_timeout = 5000;");
            ExecuteNonQuery(connection, null, "PRAGMA journal_mode = WAL;");
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
}
