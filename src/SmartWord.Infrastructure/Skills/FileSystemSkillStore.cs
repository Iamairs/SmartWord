using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.Persistence;

namespace SmartWord.Infrastructure.Skills
{
    /// <summary>
    /// 基于本地文件夹管理 SmartWord Skill。首版只加载资源，不执行 scripts。
    /// </summary>
    public sealed class FileSystemSkillStore : ISkillStore
    {
        private const int MaxSkillMarkdownBytes = 64 * 1024;
        private const string SkillFileName = "SKILL.md";
        private const string StateFileName = "skills-state.json";

        private readonly string _builtInRoot;
        private readonly string _userRoot;

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

                File.WriteAllText(Path.Combine(skillRoot, SkillFileName), content);
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
                File.WriteAllText(Path.Combine(skillRoot, SkillFileName), content);
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
            definition.RootPath = skillRoot;
            definition.SkillFilePath = skillFilePath;
            definition.UpdatedAtUtc = File.GetLastWriteTimeUtc(skillFilePath);
            if (states.Enabled.TryGetValue(definition.Name, out var enabled))
            {
                definition.Enabled = enabled;
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
                Resources = ListResources(definition.RootPath, cancellationToken)
            };
        }

        private static string ReadSkillContent(string skillFilePath)
        {
            var fileInfo = new FileInfo(skillFilePath);
            if (fileInfo.Length > MaxSkillMarkdownBytes)
            {
                throw new InvalidOperationException("SKILL.md 超过 64KB，已拒绝加载。");
            }

            return File.ReadAllText(skillFilePath);
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
                    });
                }
            }

            return resources
                .OrderBy(resource => resource.Kind)
                .ThenBy(resource => resource.RelativePath)
                .ToList();
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
version: 1.0.0
enabled: true
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

- 不执行 `scripts/` 下的脚本。
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

                return JsonConvert.DeserializeObject<SkillState>(File.ReadAllText(statePath))
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
            File.WriteAllText(GetStatePath(), JsonConvert.SerializeObject(state ?? new SkillState(), Formatting.Indented));
        }

        private string GetStatePath()
        {
            return Path.Combine(_userRoot, StateFileName);
        }

        private sealed class SkillState
        {
            public Dictionary<string, bool> Enabled { get; set; } =
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
