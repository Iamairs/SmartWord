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
    /// 使用本地 SQLite 按文档隔离持久化对话历史。
    /// </summary>
    public class SqliteConversationStore : IConversationStore
    {
        private readonly SmartWordSqliteDatabase _database;

        public SqliteConversationStore()
            : this(new SmartWordSqliteDatabase())
        {
        }

        public SqliteConversationStore(SmartWordSqliteDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public Task AppendUserMessageAsync(
            string documentPath,
            AgentMessage message,
            CancellationToken cancellationToken)
        {
            return AppendMessageAsync(documentPath, message, cancellationToken);
        }

        public Task AppendAssistantMessageAsync(
            string documentPath,
            AgentMessage message,
            CancellationToken cancellationToken)
        {
            return AppendMessageAsync(documentPath, message, cancellationToken);
        }

        public Task AppendToolResultAsync(
            string documentPath,
            string toolCallId,
            string toolName,
            string rawInput,
            ToolCallResult result,
            CancellationToken cancellationToken)
        {
            var message = new AgentMessage
            {
                Role = "tool",
                ToolCallId = toolCallId ?? string.Empty,
                Name = toolName ?? string.Empty,
                Content = result == null ? string.Empty : result.Output ?? string.Empty,
                ToolName = toolName ?? string.Empty,
                RawToolInput = rawInput ?? string.Empty,
                ToolSuccess = result != null && result.Success
            };

            return AppendMessageAsync(documentPath, message, cancellationToken);
        }

        public Task<IReadOnlyList<AgentMessage>> GetHistoryAsync(
            string documentPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run<IReadOnlyList<AgentMessage>>(() =>
            {
                var items = new List<AgentMessage>();
                using (var connection = _database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT role, content, reasoning_content, tool_call_id, name, tool_name,
       raw_tool_input, tool_success, tool_calls_json, is_compressed_summary,
       is_internal_observation, internal_observation_kind
FROM conversation_messages
WHERE document_key = $document_key
ORDER BY id ASC;";
                    command.Parameters.AddWithValue("$document_key", _database.CreateDocumentKey(documentPath));
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            items.Add(ReadMessage(reader));
                        }
                    }
                }

                return items.AsReadOnly();
            }, cancellationToken);
        }

        public int EstimateTokenCount(IReadOnlyCollection<AgentMessage> messages)
        {
            if (messages == null)
            {
                return 0;
            }

            var total = 0;
            foreach (var message in messages)
            {
                if (message == null)
                {
                    continue;
                }

                total += (message.Content ?? string.Empty).Length;
                total += (message.ReasoningContent ?? string.Empty).Length;
                if (message.ToolCalls != null)
                {
                    total += message.ToolCalls.Sum(item => (item == null ? 0 : (item.Input ?? string.Empty).Length));
                }
            }

            return total / 2;
        }

        private Task AppendMessageAsync(
            string documentPath,
            AgentMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() =>
            {
                var safeMessage = message ?? new AgentMessage();
                using (var connection = _database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO conversation_messages(
    document_key, document_path, role, content, reasoning_content,
    tool_call_id, name, tool_name, raw_tool_input, tool_success,
    tool_calls_json, is_compressed_summary, is_internal_observation,
    internal_observation_kind, created_at_utc)
VALUES(
    $document_key, $document_path, $role, $content, $reasoning_content,
    $tool_call_id, $name, $tool_name, $raw_tool_input, $tool_success,
    $tool_calls_json, $is_compressed_summary, $is_internal_observation,
    $internal_observation_kind, $created_at_utc);";
                    command.Parameters.AddWithValue("$document_key", _database.CreateDocumentKey(documentPath));
                    command.Parameters.AddWithValue("$document_path", NormalizeDocumentPath(documentPath));
                    command.Parameters.AddWithValue("$role", safeMessage.Role ?? string.Empty);
                    command.Parameters.AddWithValue("$content", (object)(SecretRedactor.Redact(safeMessage.Content) ?? string.Empty));
                    command.Parameters.AddWithValue("$reasoning_content", (object)(SecretRedactor.Redact(safeMessage.ReasoningContent) ?? string.Empty));
                    command.Parameters.AddWithValue("$tool_call_id", (object)(safeMessage.ToolCallId ?? string.Empty));
                    command.Parameters.AddWithValue("$name", (object)(safeMessage.Name ?? string.Empty));
                    command.Parameters.AddWithValue("$tool_name", (object)(safeMessage.ToolName ?? string.Empty));
                    command.Parameters.AddWithValue("$raw_tool_input", (object)(SecretRedactor.Redact(safeMessage.RawToolInput) ?? string.Empty));
                    command.Parameters.AddWithValue("$tool_success", safeMessage.ToolSuccess ? 1 : 0);
                    command.Parameters.AddWithValue(
                        "$tool_calls_json",
                        (object)SecretRedactor.Redact(JsonConvert.SerializeObject(safeMessage.ToolCalls ?? new List<ToolCall>())));
                    command.Parameters.AddWithValue("$is_compressed_summary", safeMessage.IsCompressedSummary ? 1 : 0);
                    command.Parameters.AddWithValue("$is_internal_observation", safeMessage.IsInternalObservation ? 1 : 0);
                    command.Parameters.AddWithValue("$internal_observation_kind", (object)(safeMessage.InternalObservationKind ?? string.Empty));
                    command.Parameters.AddWithValue("$created_at_utc", DateTimeOffset.UtcNow.ToString("O"));
                    command.ExecuteNonQuery();
                    transaction.Commit();
                }
            }, cancellationToken);
        }

        private static AgentMessage ReadMessage(SqliteDataReader reader)
        {
            var toolCallsJson = ReadString(reader, "tool_calls_json");
            var toolCalls = new List<ToolCall>();
            if (!string.IsNullOrWhiteSpace(toolCallsJson))
            {
                try
                {
                    toolCalls = JsonConvert.DeserializeObject<List<ToolCall>>(toolCallsJson) ?? new List<ToolCall>();
                }
                catch (JsonException)
                {
                    toolCalls = new List<ToolCall>();
                }
            }

            return new AgentMessage
            {
                Role = ReadString(reader, "role"),
                Content = ReadString(reader, "content"),
                ReasoningContent = ReadString(reader, "reasoning_content"),
                ToolCallId = ReadString(reader, "tool_call_id"),
                Name = ReadString(reader, "name"),
                ToolName = ReadString(reader, "tool_name"),
                RawToolInput = ReadString(reader, "raw_tool_input"),
                ToolSuccess = ReadInt(reader, "tool_success") == 1,
                ToolCalls = toolCalls,
                IsCompressedSummary = ReadInt(reader, "is_compressed_summary") == 1,
                IsInternalObservation = ReadInt(reader, "is_internal_observation") == 1,
                InternalObservationKind = ReadString(reader, "internal_observation_kind")
            };
        }

        private static string NormalizeDocumentPath(string documentPath)
        {
            return string.IsNullOrWhiteSpace(documentPath) ? "__active_document__" : documentPath;
        }

        private static string ReadString(IDataRecord reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static int ReadInt(IDataRecord reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
        }
    }
}
