using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SmartWord.Core.Telemetry;
using SmartWord.Infrastructure.Telemetry;
using Xunit;

namespace SmartWord.Application.Tests.Telemetry
{
    public sealed class EvalTelemetrySinkTests
    {
        [Fact]
        public async Task JsonlAgentTelemetrySink_RecordAsync_WritesOneJsonLine()
        {
            var directory = CreateTempDirectory();
            var tracePath = Path.Combine(directory, "trace.jsonl");
            var sink = new JsonlAgentTelemetrySink(tracePath);

            await sink.RecordAsync(CreateToolEvent("tool_call_completed"), CancellationToken.None);

            var lines = File.ReadAllLines(tracePath);
            Assert.Single(lines);
            Assert.Contains("\"eventType\":\"tool_call_completed\"", lines[0]);
            Assert.Contains("\"caseId\":\"case_001\"", lines[0]);
        }

        [Fact]
        public async Task SqliteEvalTelemetrySink_RecordAsync_ProjectsToolCallTable()
        {
            var directory = CreateTempDirectory();
            var databasePath = Path.Combine(directory, "eval.sqlite");
            var sink = new SqliteEvalTelemetrySink(databasePath);

            await sink.RecordAsync(CreateToolEvent("tool_call_completed"), CancellationToken.None);

            using (var connection = new SqliteConnection("Data Source=" + databasePath))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT tool_name, success FROM eval_tool_calls WHERE case_id = $case_id";
                    command.Parameters.AddWithValue("$case_id", "case_001");
                    using (var reader = command.ExecuteReader())
                    {
                        Assert.True(reader.Read());
                        Assert.Equal("grep_document", reader.GetString(0));
                        Assert.Equal(1, reader.GetInt32(1));
                    }
                }
            }
        }

        [Fact]
        public async Task CompositeTelemetrySink_RecordAsync_WritesAllSinks()
        {
            var directory = CreateTempDirectory();
            var tracePath = Path.Combine(directory, "trace.jsonl");
            var databasePath = Path.Combine(directory, "eval.sqlite");
            var sink = new CompositeTelemetrySink(
                new JsonlAgentTelemetrySink(tracePath),
                new SqliteEvalTelemetrySink(databasePath));

            await sink.RecordAsync(CreateToolEvent("tool_call_completed"), CancellationToken.None);

            Assert.True(File.Exists(tracePath));
            Assert.True(File.Exists(databasePath));
            Assert.Single(File.ReadLines(tracePath));
        }

        private static AgentTelemetryEvent CreateToolEvent(string eventType)
        {
            var e = AgentTelemetryEvent.Create(eventType);
            e.EvalRunId = "run_001";
            e.TaskRunId = "task_001";
            e.CaseId = "case_001";
            e.Level = "L1";
            e.Variant = "smartword";
            e.Mode = "Agent";
            e.PermissionMode = "ConfirmWrites";
            e.Model = "test-model";
            e.Data["toolCallId"] = "tool_001";
            e.Data["toolName"] = "grep_document";
            e.Data["rawInput"] = "{\"keyword\":\"AI\"}";
            e.Data["operationDescription"] = "搜索关键词";
            e.Data["durationMs"] = 12;
            e.Data["success"] = true;
            e.Data["requiresConfirmation"] = false;
            e.Data["wasConfirmed"] = false;
            return e;
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "SmartWordTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
