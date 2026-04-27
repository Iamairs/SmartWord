using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.Skills;
using Xunit;

namespace SmartWord.Application.Tests.Infrastructure
{
    public sealed class FileSystemSkillStoreTests
    {
        [Fact]
        public async Task CreateSkillAsync_ValidRequest_PersistsSkillTemplate()
        {
            var roots = CreateTempRoots();
            try
            {
                var store = new FileSystemSkillStore(roots.BuiltInRoot, roots.UserRoot);

                var detail = await store.CreateSkillAsync(
                    new CreateSkillRequest
                    {
                        Name = "document-finalizer",
                        DisplayName = "文档终检",
                        Description = "交付前检查当前 Word 文档。"
                    },
                    CancellationToken.None);

                Assert.Equal("document-finalizer", detail.Definition.Name);
                Assert.False(detail.Definition.IsBuiltIn);
                Assert.Contains("`scripts/` 下的脚本只能通过 `skill_run_script`", detail.Content);

                var reloaded = await store.GetSkillDetailAsync("document-finalizer", CancellationToken.None);
                Assert.NotNull(reloaded);
                Assert.Contains("交付前检查当前 Word 文档。", reloaded.Content);
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public async Task CreateSkillAsync_InvalidName_RejectsRequest()
        {
            var roots = CreateTempRoots();
            try
            {
                var store = new FileSystemSkillStore(roots.BuiltInRoot, roots.UserRoot);

                await Assert.ThrowsAsync<ArgumentException>(() => store.CreateSkillAsync(
                    new CreateSkillRequest { Name = "..\\bad" },
                    CancellationToken.None));
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public async Task DeleteSkillAsync_BuiltInSkill_RejectsDelete()
        {
            var roots = CreateTempRoots();
            try
            {
                WriteSkill(roots.BuiltInRoot, "contract-review", "合同审阅");
                var store = new FileSystemSkillStore(roots.BuiltInRoot, roots.UserRoot);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    store.DeleteSkillAsync("contract-review", CancellationToken.None));
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public async Task SetSkillEnabledAsync_DisabledSkill_UpdatesListState()
        {
            var roots = CreateTempRoots();
            try
            {
                WriteSkill(roots.BuiltInRoot, "business-report-polish", "商务报告润色");
                var store = new FileSystemSkillStore(roots.BuiltInRoot, roots.UserRoot);

                await store.SetSkillEnabledAsync("business-report-polish", false, CancellationToken.None);
                var skills = await store.GetSkillsAsync(CancellationToken.None);

                Assert.False(skills.Single(skill => skill.Name == "business-report-polish").Enabled);
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public async Task GetSkillDetailAsync_WithScripts_ListsScriptWithoutReadingExecution()
        {
            var roots = CreateTempRoots();
            try
            {
                WriteSkill(roots.UserRoot, "term-check", "术语检查");
                var scriptPath = Path.Combine(roots.UserRoot, "term-check", "scripts", "scan.py");
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                File.WriteAllText(scriptPath, "print('do not execute')");
                var store = new FileSystemSkillStore(roots.BuiltInRoot, roots.UserRoot);

                var detail = await store.GetSkillDetailAsync("term-check", CancellationToken.None);

                Assert.Contains(detail.Resources, resource =>
                    resource.Kind == "scripts" && resource.RelativePath == "scripts/scan.py");
                Assert.Contains(detail.Scripts, script =>
                    script.Runtime == "python"
                    && script.RelativePath == "scripts/scan.py"
                    && script.Sha256.Length == 64);
                Assert.DoesNotContain("do not execute", detail.Content);
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public async Task ResolveScriptAsync_PathTraversal_RejectsRequest()
        {
            var roots = CreateTempRoots();
            try
            {
                WriteSkill(roots.UserRoot, "term-check", "术语检查");
                var store = new FileSystemSkillStore(roots.BuiltInRoot, roots.UserRoot);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    store.ResolveScriptAsync("term-check", "../outside.py", "python", CancellationToken.None));
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public async Task ResolveScriptAsync_ScriptHashChanges_ReturnsNewHash()
        {
            var roots = CreateTempRoots();
            try
            {
                WriteSkill(roots.UserRoot, "term-check", "术语检查");
                var scriptPath = Path.Combine(roots.UserRoot, "term-check", "scripts", "scan.py");
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                File.WriteAllText(scriptPath, "print('v1')");
                var store = new FileSystemSkillStore(roots.BuiltInRoot, roots.UserRoot);

                var first = await store.ResolveScriptAsync("term-check", "scripts/scan.py", "python", CancellationToken.None);
                File.WriteAllText(scriptPath, "print('v2')");
                var second = await store.ResolveScriptAsync("term-check", "scripts/scan.py", "python", CancellationToken.None);

                Assert.NotEqual(first.Script.Sha256, second.Script.Sha256);
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public void SkillPathGuard_CombineSkillRoot_PathTraversal_RejectsRequest()
        {
            var root = Path.Combine(Path.GetTempPath(), "smartword-skill-root-" + Guid.NewGuid().ToString("N"));

            Assert.Throws<ArgumentException>(() => SkillPathGuard.CombineSkillRoot(root, "../outside"));
        }

        private static TempRoots CreateTempRoots()
        {
            var root = Path.Combine(Path.GetTempPath(), "smartword-skills-" + Guid.NewGuid().ToString("N"));
            return new TempRoots
            {
                Root = root,
                BuiltInRoot = Path.Combine(root, "built-in"),
                UserRoot = Path.Combine(root, "user")
            };
        }

        private static void WriteSkill(string root, string name, string displayName)
        {
            var skillRoot = Path.Combine(root, name);
            Directory.CreateDirectory(skillRoot);
            File.WriteAllText(
                Path.Combine(skillRoot, "SKILL.md"),
$@"---
name: {name}
display_name: {displayName}
description: 用于测试的 Skill。
version: 1.0.0
enabled: true
---

# {displayName}

测试正文。
");
        }

        private static void DeleteTempRoots(TempRoots roots)
        {
            if (Directory.Exists(roots.Root))
            {
                Directory.Delete(roots.Root, true);
            }
        }

        private sealed class TempRoots
        {
            public string Root { get; set; }

            public string BuiltInRoot { get; set; }

            public string UserRoot { get; set; }
        }
    }
}
