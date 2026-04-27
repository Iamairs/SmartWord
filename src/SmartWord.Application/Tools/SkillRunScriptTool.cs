using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Tools
{
    /// <summary>
    /// 通过受控 runner 执行 Skill scripts/ 目录中的本地自动化脚本。
    /// </summary>
    public sealed class SkillRunScriptTool : ITool
    {
        private readonly ISkillStore _skillStore;
        private readonly ISkillScriptRunner _runner;

        public SkillRunScriptTool(ISkillStore skillStore, ISkillScriptRunner runner)
        {
            _skillStore = skillStore ?? throw new ArgumentNullException(nameof(skillStore));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public string Name => "skill_run_script";

        public string Description =>
            "执行指定 Skill 的 scripts/ 下 C# 或 Python 脚本。脚本只能做本地分析和生成输出，不能直接修改 Word；如需修改 Word，必须后续调用 patch_range 或 execute_script。";

        public ToolPermission RequiredPermission => ToolPermission.LocalAutomation;

        public bool IsVisibleToModel => true;

        public JsonElement InputSchema => JsonDocument.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""skill_name"": { ""type"": ""string"", ""description"": ""Skill 名称。"" },
    ""script_path"": { ""type"": ""string"", ""description"": ""scripts/ 下脚本相对路径，例如 scripts/analyze.py。"" },
    ""runtime"": { ""type"": ""string"", ""enum"": [""csharp"", ""python""] },
    ""arguments_json"": { ""type"": ""string"", ""description"": ""传给脚本的 JSON 字符串。"" },
    ""confirmed_input_paths"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"" },
      ""description"": ""用户已确认允许作为输入副本导入 workspace 的本地路径。""
    },
    ""expected_outputs"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"" },
      ""description"": ""预期脚本会生成的输出文件名或说明。""
    },
    ""purpose"": { ""type"": ""string"", ""description"": ""执行脚本的目的和后续使用方式。"" }
  },
  ""required"": [""skill_name"", ""script_path"", ""runtime"", ""purpose""],
  ""additionalProperties"": false
}").RootElement.Clone();

        public async Task<ToolCallResult> ExecuteAsync(
            JsonElement input,
            IUndoScope undoScope,
            CancellationToken cancellationToken)
        {
            try
            {
                var parsed = JObject.Parse(input.GetRawText());
                var request = await BuildRequestAsync(parsed, cancellationToken).ConfigureAwait(false);
                var result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
                var payload = new
                {
                    success = result.Success,
                    stdout = result.Stdout,
                    stderr = result.Stderr,
                    exit_code = result.ExitCode,
                    duration_ms = result.DurationMs,
                    outputs = result.Outputs,
                    result_json = result.ResultJson,
                    warnings = result.Warnings,
                    workspace_path = result.WorkspacePath,
                    script = new
                    {
                        skill_name = request.Resolution.Script.SkillName,
                        script_path = request.Resolution.Script.RelativePath,
                        runtime = request.Resolution.Script.Runtime,
                        sha256 = request.Resolution.Script.Sha256
                    }
                };

                var output = JsonConvert.SerializeObject(payload, Formatting.Indented);
                return result.Success
                    ? ToolCallResult.Ok(output, metadata: payload, operationDescription: request.Purpose)
                    : ToolCallResult.Error(Name, output);
            }
            catch (Exception ex)
            {
                return ToolCallResult.Error(Name, ex.Message);
            }
        }

        public async Task<SkillScriptApprovalKey> BuildApprovalKeyAsync(
            JObject input,
            CancellationToken cancellationToken)
        {
            var request = await BuildRequestAsync(input, cancellationToken).ConfigureAwait(false);
            return BuildApprovalKey(request);
        }

        public async Task<JObject> BuildConfirmationInputAsync(
            JObject input,
            CancellationToken cancellationToken)
        {
            var request = await BuildRequestAsync(input, cancellationToken).ConfigureAwait(false);
            var clone = input == null ? new JObject() : (JObject)input.DeepClone();
            clone["script_hash"] = request.Resolution.Script.Sha256;
            clone["script_size_bytes"] = request.Resolution.Script.SizeBytes;
            clone["normalized_script_path"] = request.Resolution.Script.RelativePath;
            clone["network"] = "disabled_by_default";
            clone["timeout_seconds"] = 30;
            clone["permission_set"] = BuildPermissionSet(request);
            return clone;
        }

        private async Task<SkillScriptRunRequest> BuildRequestAsync(
            JObject input,
            CancellationToken cancellationToken)
        {
            if (input == null)
            {
                throw new InvalidOperationException("skill_run_script 输入不能为空。");
            }

            var skillName = input.Value<string>("skill_name") ?? string.Empty;
            var scriptPath = input.Value<string>("script_path") ?? string.Empty;
            var runtime = NormalizeRuntime(input.Value<string>("runtime"));
            var resolution = await _skillStore
                .ResolveScriptAsync(skillName, scriptPath, runtime, cancellationToken)
                .ConfigureAwait(false);

            return new SkillScriptRunRequest
            {
                SkillName = resolution.Skill.Name,
                ScriptPath = resolution.Script.RelativePath,
                Runtime = resolution.Script.Runtime,
                ArgumentsJson = NormalizeArgumentsJson(input.Value<string>("arguments_json")),
                ConfirmedInputPaths = ReadStringArray(input["confirmed_input_paths"]),
                ExpectedOutputs = ReadStringArray(input["expected_outputs"]),
                Purpose = input.Value<string>("purpose") ?? string.Empty,
                Resolution = resolution
            };
        }

        private static SkillScriptApprovalKey BuildApprovalKey(SkillScriptRunRequest request)
        {
            return new SkillScriptApprovalKey
            {
                SkillName = request.Resolution.Script.SkillName,
                RelativeScriptPath = request.Resolution.Script.RelativePath,
                ScriptHash = request.Resolution.Script.Sha256,
                Runtime = request.Resolution.Script.Runtime,
                PermissionSet = BuildPermissionSet(request)
            };
        }

        private static string BuildPermissionSet(SkillScriptRunRequest request)
        {
            var payload = new
            {
                confirmed_input_paths = (request.ConfirmedInputPaths ?? new List<string>())
                    .Select(item => (item ?? string.Empty).Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                expected_outputs = (request.ExpectedOutputs ?? new List<string>())
                    .Select(item => (item ?? string.Empty).Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
            var json = JsonConvert.SerializeObject(payload, Formatting.None);
            using (var sha256 = SHA256.Create())
            {
                return BitConverter
                    .ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(json)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private static string NormalizeRuntime(string runtime)
        {
            var value = (runtime ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "csx":
                case "c#":
                case "csharp":
                    return "csharp";
                case "py":
                case "python":
                    return "python";
                default:
                    return value;
            }
        }

        private static string NormalizeArgumentsJson(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                return "{}";
            }

            JToken.Parse(argumentsJson);
            return argumentsJson;
        }

        private static IReadOnlyList<string> ReadStringArray(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return new List<string>();
            }

            if (token.Type != JTokenType.Array)
            {
                throw new InvalidOperationException("路径和输出字段必须是字符串数组。");
            }

            return token
                .Select(item => (item.Value<string>() ?? string.Empty).Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToList();
        }
    }
}
