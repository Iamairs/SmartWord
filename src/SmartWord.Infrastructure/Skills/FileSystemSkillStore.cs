using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.Persistence;

namespace SmartWord.Infrastructure.Skills
{
    /// <summary>
    /// 基于本地文件夹管理 SmartWord Skill，并为受控脚本执行提供路径解析。
    /// </summary>
    public sealed class FileSystemSkillStore : ISkillStore
    {
        private const int MaxSkillMarkdownBytes = 64 * 1024;
        private const string SkillFileName = "SKILL.md";
        private const string StateFileName = "skills-state.json";
        private const int MaxHistoryVersions = 5;

        private readonly string _builtInRoot;
        private readonly string _userRoot;
        private readonly object _writeSync = new object();

        public FileSystemSkillStore()
            : this(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Skills"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SmartWord",
                    "skills"))
        {
        }

        public FileSystemSkillStore(string builtInRoot, string userRoot)
        {
            _builtInRoot = Path.GetFullPath(builtInRoot ?? string.Empty);
            _userRoot = Path.GetFullPath(userRoot ?? string.Empty);
        }

        public Task<IReadOnlyList<SkillDefinition>> GetSkillsAsync(CancellationToken cancellationToken)
        {
            return Task.Run<IReadOnlyList<SkillDefinition>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var states = LoadState();
                var skills = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

                foreach (var skill in ReadSkillsFromRoot(_builtInRoot, true, states, cancellationToken))
                {
                    skills[skill.Name] = skill;
                }

                foreach (var skill in ReadSkillsFromRoot(_userRoot, false, states, cancellationToken))
                {
                    skills[skill.Name] = skill;
                }

                return skills.Values
                    .OrderBy(skill => skill.IsBuiltIn ? 0 : 1)
                    .ThenBy(skill => skill.DisplayName)
                    .ToList();
            }, cancellationToken);
        }

        public async Task<SkillDetail> GetSkillDetailAsync(string name, CancellationToken cancellationToken)
        {
            var definition = await FindSkillAsync(name, cancellationToken).ConfigureAwait(false);
            if (definition == null)
            {
                return null;
            }

            return await Task.Run(() => ReadDetail(definition, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<SkillDetail> CreateSkillAsync(CreateSkillRequest request, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request == null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                var name = SkillPathGuard.NormalizeSkillName(request.Name);
                SkillPathGuard.EnsureValidSkillName(name);
                Directory.CreateDirectory(_userRoot);

                if (SkillExists(name))
                {
                    throw new InvalidOperationException("同名 Skill 已存在。");
                }

                var skillRoot = SkillPathGuard.CombineSkillRoot(_userRoot, name);
                Directory.CreateDirectory(skillRoot);
                Directory.CreateDirectory(Path.Combine(skillRoot, "references"));
                Directory.CreateDirectory(Path.Combine(skillRoot, "assets"));
                Directory.CreateDirectory(Path.Combine(skillRoot, "scripts"));

                var content = string.IsNullOrWhiteSpace(request.Content)
                    ? BuildSkillTemplate(name, request.DisplayName, request.Description)
                    : NormalizeSkillContent(name, request.Content);

                WriteTextAtomically(Path.Combine(skillRoot, SkillFileName), content);
                return ReadDetail(ReadDefinition(skillRoot, false, LoadState()), cancellationToken);
            }, cancellationToken);
        }

        public Task<SkillDetail> SaveSkillAsync(SaveSkillRequest request, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request == null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                var name = SkillPathGuard.NormalizeSkillName(request.Name);
                SkillPathGuard.EnsureValidSkillName(name);
                var skillRoot = SkillPathGuard.CombineSkillRoot(_userRoot, name);
                if (!Directory.Exists(skillRoot))
                {
                    throw new InvalidOperationException("只能编辑用户 Skill。");
                }

                var content = NormalizeSkillContent(name, request.Content);
                var skillFilePath = Path.Combine(skillRoot, SkillFileName);
                lock (_writeSync)
                {
                    if (!string.IsNullOrWhiteSpace(request.ExpectedContentSha256)
                        && File.Exists(skillFilePath)
                        && !string.Equals(
                            ComputeSha256(skillFilePath),
                            request.ExpectedContentSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Skill 已在其他位置更新，请刷新后再保存。");
                    }

                    PreserveHistoryVersion(skillRoot, skillFilePath);
                    WriteTextAtomically(skillFilePath, content);
                    TrimHistoryVersions(skillRoot);
                }

                return ReadDetail(ReadDefinition(skillRoot, false, LoadState()), cancellationToken);
            }, cancellationToken);
        }

        public async Task DeleteSkillAsync(string name, CancellationToken cancellationToken)
        {
            var definition = await FindSkillAsync(name, cancellationToken).ConfigureAwait(false);
            if (definition == null)
            {
                return;
            }

            if (definition.IsBuiltIn)
            {
                throw new InvalidOperationException("内置 Skill 不允许删除，只能禁用。");
            }

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SkillPathGuard.EnsureInsideRoot(_userRoot, definition.RootPath);
                Directory.Delete(definition.RootPath, true);
                var states = LoadState();
                states.Enabled.Remove(definition.Name);
                SaveState(states);
            }, cancellationToken).ConfigureAwait(false);
        }

        public Task SetSkillEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definition = await FindSkillAsync(name, cancellationToken).ConfigureAwait(false);
                if (definition == null)
                {
                    throw new InvalidOperationException("未找到指定 Skill。");
                }

                var states = LoadState();
                states.Enabled[definition.Name] = enabled;
                SaveState(states);
            }, cancellationToken);
        }

        public Task SetSkillScriptPolicyAsync(
            string name,
            SkillScriptPolicy policy,
            CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definition = await FindSkillAsync(name, cancellationToken).ConfigureAwait(false);
                if (definition == null)
                {
                    throw new InvalidOperationException("未找到指定 Skill。");
                }

                var states = LoadState();
                states.ScriptPolicies[definition.Name] = policy.ToString();
                SaveState(states);
            }, cancellationToken);
        }

        public async Task<IReadOnlyList<SkillScriptInfo>> GetSkillScriptsAsync(
            string name,
            CancellationToken cancellationToken)
        {
            var definition = await FindSkillAsync(name, cancellationToken).ConfigureAwait(false);
            if (definition == null)
            {
                return new List<SkillScriptInfo>();
            }

            return await Task.Run(
                    () => ListScripts(definition, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<SkillScriptResolution> ResolveScriptAsync(
            string skillName,
            string scriptPath,
            string runtime,
            CancellationToken cancellationToken)
        {
            var definition = await FindSkillAsync(skillName, cancellationToken).ConfigureAwait(false);
            if (definition == null || !definition.Enabled)
            {
                throw new InvalidOperationException("未找到可用的 Skill。");
            }

            if (definition.ScriptPolicy == SkillScriptPolicy.Disabled)
            {
                throw new InvalidOperationException("此 Skill 的脚本默认禁用，请先在 Skill 面板中显式启用。");
            }

            return await Task.Run(
                    () => ResolveScript(definition, scriptPath, runtime, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<SkillResourceResolution> ResolveResourceAsync(
            string skillName,
            string relativePath,
            CancellationToken cancellationToken)
        {
            var definition = await FindSkillAsync(skillName, cancellationToken).ConfigureAwait(false);
            if (definition == null || !definition.Enabled)
            {
                throw new InvalidOperationException("未找到可用的 Skill。");
            }

            return await Task.Run(
                    () => ResolveResource(definition, relativePath, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<SkillDefinition> FindSkillAsync(string name, CancellationToken cancellationToken)
        {
            var safeName = SkillPathGuard.NormalizeSkillName(name);
            SkillPathGuard.EnsureValidSkillName(safeName);
            var skills = await GetSkillsAsync(cancellationToken).ConfigureAwait(false);
            return skills.FirstOrDefault(skill => string.Equals(skill.Name, safeName, StringComparison.OrdinalIgnoreCase));
        }

        private bool SkillExists(string name)
        {
            return Directory.Exists(SkillPathGuard.CombineSkillRoot(_userRoot, name))
                || Directory.Exists(SkillPathGuard.CombineSkillRoot(_builtInRoot, name));
        }

        private IEnumerable<SkillDefinition> ReadSkillsFromRoot(
            string root,
            bool isBuiltIn,
            SkillState states,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                yield break;
            }

            foreach (var directory in Directory.GetDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                if (!SkillPathGuard.IsValidSkillName(name))
                {
                    continue;
                }

                var skillFilePath = Path.Combine(directory, SkillFileName);
                if (!File.Exists(skillFilePath))
                {
                    continue;
                }

                SkillDefinition definition;
                try
                {
                    definition = ReadDefinition(directory, isBuiltIn, states);
                }
                catch
                {
                    continue;
                }

                yield return definition;
            }
        }

        private SkillDefinition ReadDefinition(string skillRoot, bool isBuiltIn, SkillState states)
        {
            var skillName = Path.GetFileName(skillRoot);
            SkillPathGuard.EnsureValidSkillName(skillName);
            SkillPathGuard.EnsureInsideRoot(isBuiltIn ? _builtInRoot : _userRoot, skillRoot);
            var skillFilePath = Path.Combine(skillRoot, SkillFileName);
            var content = ReadSkillContent(skillFilePath);
            var definition = SkillFrontmatterParser.Parse(content, skillName);

            if (!string.Equals(definition.Name, skillName, StringComparison.OrdinalIgnoreCase))
            {
                definition.Name = SkillPathGuard.NormalizeSkillName(skillName);
            }

            definition.IsBuiltIn = isBuiltIn;
            definition.TrustLevel = isBuiltIn ? SkillTrustLevel.BuiltIn : definition.TrustLevel;
            definition.Source = isBuiltIn
                ? "built_in"
                : string.IsNullOrWhiteSpace(definition.Source) ? "local" : definition.Source;
            definition.ContentSha256 = ComputeSha256(skillFilePath);
            definition.RootPath = skillRoot;
            definition.SkillFilePath = skillFilePath;
            definition.UpdatedAtUtc = File.GetLastWriteTimeUtc(skillFilePath);
            if (states.Enabled.TryGetValue(definition.Name, out var enabled))
            {
                definition.Enabled = enabled;
            }

            definition.ScriptPolicy = definition.TrustLevel == SkillTrustLevel.External
                ? SkillScriptPolicy.Disabled
                : SkillScriptPolicy.Prompt;
            if (states.ScriptPolicies.TryGetValue(definition.Name, out var scriptPolicy)
                && Enum.TryParse(scriptPolicy, true, out SkillScriptPolicy parsedPolicy))
            {
                definition.ScriptPolicy = parsedPolicy;
            }

            return definition;
        }

        private SkillDetail ReadDetail(SkillDefinition definition, CancellationToken cancellationToken)
        {
            if (definition == null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new SkillDetail
            {
                Definition = definition,
                Content = ReadSkillContent(definition.SkillFilePath),
                Resources = ListResources(definition.RootPath, cancellationToken),
                Scripts = ListScripts(definition, cancellationToken)
            };
        }

        private static string ReadSkillContent(string skillFilePath)
        {
            var fileInfo = new FileInfo(skillFilePath);
            if (fileInfo.Length > MaxSkillMarkdownBytes)
            {
                throw new InvalidOperationException("SKILL.md 超过 64KB，已拒绝加载。");
            }

            return File.ReadAllText(skillFilePath, Encoding.UTF8);
        }

        private static IReadOnlyList<SkillResource> ListResources(string root, CancellationToken cancellationToken)
        {
            var resources = new List<SkillResource>();
            foreach (var folder in new[] { "references", "assets", "scripts" })
            {
                var folderPath = Path.Combine(root, folder);
                if (!Directory.Exists(folderPath))
                {
                    continue;
                }

                foreach (var filePath in Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = filePath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    resources.Add(new SkillResource
                    {
                        RelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                        Kind = folder,
                        SizeBytes = new FileInfo(filePath).Length
                        ,
                        IsText = IsTextResource(filePath)
                    });
                }
            }

            return resources
                .OrderBy(resource => resource.Kind)
                .ThenBy(resource => resource.RelativePath)
                .ToList();
        }

        private static IReadOnlyList<SkillScriptInfo> ListScripts(
            SkillDefinition definition,
            CancellationToken cancellationToken)
        {
            var scripts = new List<SkillScriptInfo>();
            var scriptsRoot = Path.Combine(definition.RootPath, "scripts");
            if (!Directory.Exists(scriptsRoot))
            {
                return scripts;
            }

            SkillPathGuard.EnsureInsideRoot(definition.RootPath, scriptsRoot);
            foreach (var filePath in Directory.GetFiles(scriptsRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SkillPathGuard.EnsureInsideRoot(scriptsRoot, filePath);
                var runtime = DetectRuntime(filePath);
                if (string.IsNullOrWhiteSpace(runtime))
                {
                    continue;
                }

                var relativePath = filePath
                    .Substring(definition.RootPath.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                scripts.Add(new SkillScriptInfo
                {
                    SkillName = definition.Name,
                    RelativePath = relativePath,
                    Runtime = runtime,
                    SizeBytes = new FileInfo(filePath).Length,
                    Sha256 = ComputeSha256(filePath)
                });
            }

            return scripts
                .OrderBy(script => script.RelativePath)
                .ToList();
        }

        private static SkillScriptResolution ResolveScript(
            SkillDefinition definition,
            string scriptPath,
            string runtime,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRuntime = NormalizeRuntime(runtime);
            if (string.IsNullOrWhiteSpace(normalizedRuntime))
            {
                throw new InvalidOperationException("runtime 仅支持 csharp 或 python。");
            }

            var normalizedRelativePath = NormalizeScriptRelativePath(scriptPath);
            var scriptsRoot = Path.Combine(definition.RootPath, "scripts");
            var absolutePath = Path.GetFullPath(Path.Combine(definition.RootPath, normalizedRelativePath));
            SkillPathGuard.EnsureInsideRoot(scriptsRoot, absolutePath);
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException("未找到指定 Skill 脚本。", normalizedRelativePath);
            }

            var detectedRuntime = DetectRuntime(absolutePath);
            if (!string.Equals(detectedRuntime, normalizedRuntime, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("脚本扩展名与 runtime 不匹配。");
            }

            var fileInfo = new FileInfo(absolutePath);
            return new SkillScriptResolution
            {
                Skill = definition,
                AbsolutePath = absolutePath,
                Script = new SkillScriptInfo
                {
                    SkillName = definition.Name,
                    RelativePath = normalizedRelativePath,
                    Runtime = normalizedRuntime,
                    SizeBytes = fileInfo.Length,
                    Sha256 = ComputeSha256(absolutePath)
                }
            };
        }

        private static SkillResourceResolution ResolveResource(
            SkillDefinition definition,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = (relativePath ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
            if (Path.IsPathRooted(normalized)
                || normalized.Contains("../")
                || normalized == ".."
                || (!normalized.StartsWith("references/", StringComparison.OrdinalIgnoreCase)
                    && !normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("资源路径必须位于 references/ 或 assets/ 目录内。");
            }

            var absolutePath = Path.GetFullPath(Path.Combine(definition.RootPath, normalized));
            SkillPathGuard.EnsureInsideRoot(definition.RootPath, absolutePath);
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException("未找到指定 Skill 资源。", normalized);
            }

            EnsureNoReparsePoint(definition.RootPath, absolutePath);
            var fileInfo = new FileInfo(absolutePath);
            return new SkillResourceResolution
            {
                Skill = definition,
                AbsolutePath = absolutePath,
                IsText = IsTextResource(absolutePath),
                Resource = new SkillResource
                {
                    RelativePath = normalized,
                    Kind = normalized.StartsWith("references/", StringComparison.OrdinalIgnoreCase)
                        ? "references"
                        : "assets",
                    SizeBytes = fileInfo.Length,
                    IsText = IsTextResource(absolutePath)
                }
            };
        }

        private static string NormalizeScriptRelativePath(string scriptPath)
        {
            var normalized = (scriptPath ?? string.Empty).Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new InvalidOperationException("script_path 不能为空。");
            }

            if (Path.IsPathRooted(normalized) || normalized.Contains("../") || normalized == "..")
            {
                throw new InvalidOperationException("script_path 必须是 scripts/ 目录内的相对路径。");
            }

            if (!normalized.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "scripts/" + normalized.TrimStart('/');
            }

            return normalized;
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
                    return string.Empty;
            }
        }

        private static string DetectRuntime(string filePath)
        {
            var extension = Path.GetExtension(filePath ?? string.Empty).ToLowerInvariant();
            switch (extension)
            {
                case ".csx":
                    return "csharp";
                case ".py":
                    return "python";
                default:
                    return string.Empty;
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                return BitConverter
                    .ToString(sha256.ComputeHash(stream))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        private string NormalizeSkillContent(string name, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("SKILL.md 内容不能为空。");
            }

            var byteCount = System.Text.Encoding.UTF8.GetByteCount(content);
            if (byteCount > MaxSkillMarkdownBytes)
            {
                throw new InvalidOperationException("SKILL.md 超过 64KB，已拒绝保存。");
            }

            var declaredName = SkillFrontmatterParser.ReadFrontmatterName(content);
            if (!string.Equals(declaredName, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SKILL.md frontmatter 中的 name 必须与 Skill 目录名一致。");
            }

            return SecretRedactor.Redact(content);
        }

        private static string BuildSkillTemplate(string name, string displayName, string description)
        {
            var safeDisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim();
            var safeDescription = string.IsNullOrWhiteSpace(description)
                ? "面向当前 Word 文档的自定义处理流程。"
                : description.Trim();

            return SecretRedactor.Redact(
$@"---
name: {name}
display_name: {safeDisplayName}
 description: {safeDescription}
schema_version: 1
 version: 1.0.0
 enabled: true
trust_level: user
source: local
---

# {safeDisplayName}

## 工作流

1. 先判断当前 Word 文档是否符合此 Skill 的使用场景。
2. 优先读取相关段落、标题、表格或选区，避免无根据改写。
3. 给出简洁的问题清单或处理计划。
4. 需要修改文档时，只能通过 SmartWord 已有工具执行，并遵守权限确认、Undo 和验证。

## 输出要求

- 说明受影响的段落或章节。
- 区分确定问题和需要用户确认的问题。
- 对写入类操作给出可验证的修改目标。

## 安全边界

- `scripts/` 下的脚本只能通过 `skill_run_script` 受控执行。
- 不读取或输出 API Key、访问令牌、Authorization 头等密钥。
- 不绕过 SmartWord 的权限确认和任务审计。
");
        }

        private SkillState LoadState()
        {
            try
            {
                var statePath = GetStatePath();
                if (!File.Exists(statePath))
                {
                    return new SkillState();
                }

                return JsonConvert.DeserializeObject<SkillState>(File.ReadAllText(statePath, Encoding.UTF8))
                    ?? new SkillState();
            }
            catch
            {
                return new SkillState();
            }
        }

        private void SaveState(SkillState state)
        {
            Directory.CreateDirectory(_userRoot);
            WriteTextAtomically(
                GetStatePath(),
                JsonConvert.SerializeObject(state ?? new SkillState(), Formatting.Indented));
        }

        private string GetStatePath()
        {
            return Path.Combine(_userRoot, StateFileName);
        }

        private sealed class SkillState
        {
            public Dictionary<string, bool> Enabled { get; set; } =
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, string> ScriptPolicies { get; set; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsTextResource(string path)
        {
            switch ((Path.GetExtension(path) ?? string.Empty).ToLowerInvariant())
            {
                case ".md":
                case ".txt":
                case ".json":
                case ".yaml":
                case ".yml":
                case ".csv":
                case ".tsv":
                case ".xml":
                case ".html":
                case ".css":
                    return true;
                default:
                    return false;
            }
        }

        private static void EnsureNoReparsePoint(string root, string path)
        {
            var current = new FileInfo(path) as FileSystemInfo;
            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Skill 资源路径包含符号链接或 Junction，已拒绝访问。");
                }

                if (string.Equals(current.FullName, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
            }
        }

        private static void WriteTextAtomically(string path, string content)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, content ?? string.Empty, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void PreserveHistoryVersion(string skillRoot, string skillFilePath)
        {
            if (!File.Exists(skillFilePath))
            {
                return;
            }

            var historyRoot = Path.Combine(skillRoot, ".history");
            Directory.CreateDirectory(historyRoot);
            var historyPath = Path.Combine(
                historyRoot,
                DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + ComputeSha256(skillFilePath) + ".md");
            File.Copy(skillFilePath, historyPath, false);
        }

        private static void TrimHistoryVersions(string skillRoot)
        {
            var historyRoot = Path.Combine(skillRoot, ".history");
            if (!Directory.Exists(historyRoot))
            {
                return;
            }

            foreach (var path in Directory.GetFiles(historyRoot, "*.md")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(MaxHistoryVersions))
            {
                File.Delete(path);
            }
        }
    }
}
