using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SmartWord.EvalRunner
{
    internal static class BenchmarkCaseLoader
    {
        public static IEnumerable<BenchmarkCase> Load(string casesRoot, EvalRunnerOptions options)
        {
            var taskFiles = Directory.EnumerateFiles(casesRoot, "task.json", SearchOption.AllDirectories)
                .OrderBy(path => path);
            foreach (var taskFile in taskFiles)
            {
                var caseDir = Path.GetDirectoryName(taskFile);
                var task = JObject.Parse(File.ReadAllText(taskFile));
                var id = task.Value<string>("id") ?? Path.GetFileName(caseDir);
                var level = task.Value<int?>("level") ?? ParseLevelFromPath(caseDir);
                if (!string.IsNullOrWhiteSpace(options.CaseId) && id != options.CaseId)
                {
                    continue;
                }

                if (options.Level > 0 && level != options.Level)
                {
                    continue;
                }

                yield return new BenchmarkCase
                {
                    Id = id,
                    Level = level,
                    Name = task.Value<string>("name") ?? id,
                    CaseDirectory = caseDir,
                    TaskJsonPath = taskFile,
                    ExpectedJsonPath = Path.Combine(caseDir, task.Value<string>("expected") ?? "expected.json"),
                    InputDocxPath = Path.Combine(caseDir, task.Value<string>("input_docx") ?? "input.docx"),
                    UserInstruction = task.Value<string>("user_instruction") ?? string.Empty,
                    Mode = task.Value<string>("mode") ?? "Agent",
                    PermissionMode = task.Value<string>("permission_mode") ?? "ConfirmWrites",
                    RequiresConfirmation = task.Value<bool?>("requires_confirmation") == true,
                    TaskJson = task
                };
            }
        }

        private static int ParseLevelFromPath(string path)
        {
            var text = path ?? string.Empty;
            for (var i = 1; i <= 4; i++)
            {
                if (text.Contains("L" + i + "_"))
                {
                    return i;
                }
            }

            return 0;
        }
    }

    internal sealed class BenchmarkCase
    {
        public string Id { get; set; } = string.Empty;
        public int Level { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CaseDirectory { get; set; } = string.Empty;
        public string TaskJsonPath { get; set; } = string.Empty;
        public string ExpectedJsonPath { get; set; } = string.Empty;
        public string InputDocxPath { get; set; } = string.Empty;
        public string UserInstruction { get; set; } = string.Empty;
        public string Mode { get; set; } = "Agent";
        public string PermissionMode { get; set; } = "ConfirmWrites";
        public bool RequiresConfirmation { get; set; }
        public JObject TaskJson { get; set; }
    }
}
