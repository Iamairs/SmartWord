using System;
using System.IO;
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
    /// 按需读取当前任务已激活 Skill 的文本资源。激活白名单由编排层强制校验。
    /// </summary>
    public sealed class ReadSkillResourceTool : ITool
    {
        private const int MaxResourceBytes = 256 * 1024;
        private const int MaxContentCharacters = 8000;
        private readonly ISkillStore _skillStore;

        public ReadSkillResourceTool(ISkillStore skillStore)
        {
            _skillStore = skillStore ?? throw new ArgumentNullException(nameof(skillStore));
        }

        public string Name => "read_skill_resource";

        public string Description =>
            "按需读取当前任务已激活 Skill 的 references/ 或文本型 assets/ 资源。只能使用资源索引中列出的相对路径。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public bool IsVisibleToModel => true;

        public JsonElement InputSchema => JsonDocument.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""skill_name"": { ""type"": ""string"" },
    ""resource_path"": { ""type"": ""string"", ""description"": ""references/ 或 assets/ 下的相对路径。"" },
    ""purpose"": { ""type"": ""string"" }
  },
  ""required"": [""skill_name"", ""resource_path"", ""purpose""],
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
                var resolution = await _skillStore.ResolveResourceAsync(
                        parsed.Value<string>("skill_name") ?? string.Empty,
                        parsed.Value<string>("resource_path") ?? string.Empty,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!resolution.IsText)
                {
                    return ToolCallResult.Error(Name, "当前资源是二进制文件，只能查看元数据，不能注入模型上下文。");
                }

                var fileInfo = new FileInfo(resolution.AbsolutePath);
                if (fileInfo.Length > MaxResourceBytes)
                {
                    return ToolCallResult.Error(Name, "Skill 文本资源超过 256KB，已拒绝读取。");
                }

                var content = File.ReadAllText(resolution.AbsolutePath, Encoding.UTF8);
                var truncated = content.Length > MaxContentCharacters;
                if (truncated)
                {
                    content = content.Substring(0, MaxContentCharacters);
                }

                var payload = new
                {
                    skill_name = resolution.Skill.Name,
                    resource_path = resolution.Resource.RelativePath,
                    size_bytes = fileInfo.Length,
                    sha256 = ComputeSha256(resolution.AbsolutePath),
                    truncated,
                    content
                };
                return ToolCallResult.Ok(
                    JsonConvert.SerializeObject(payload, Formatting.Indented),
                    metadata: payload,
                    operationDescription: parsed.Value<string>("purpose") ?? string.Empty);
            }
            catch (Exception ex)
            {
                return ToolCallResult.Error(Name, ex.Message);
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha256.ComputeHash(stream))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }
}
