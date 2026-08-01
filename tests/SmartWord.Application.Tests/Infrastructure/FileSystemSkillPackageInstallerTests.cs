using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.Skills;
using Xunit;

namespace SmartWord.Application.Tests.Infrastructure
{
    public sealed class FileSystemSkillPackageInstallerTests
    {
        [Fact]
        public async Task PreviewAndInstallFolders_MultipleValidFolders_InstallsAsExternalSkills()
        {
            var roots = CreateTempRoots();
            try
            {
                WriteSkill(roots.SourceRoot, "contract-review", "合同审阅");
                WriteSkill(roots.SourceRoot, "term-check", "术语检查");
                var installer = new FileSystemSkillPackageInstaller(roots.BuiltInRoot, roots.UserRoot);

                var preview = await installer.PreviewFoldersAsync(
                    new[]
                    {
                        Path.Combine(roots.SourceRoot, "contract-review"),
                        Path.Combine(roots.SourceRoot, "term-check")
                    },
                    CancellationToken.None);

                Assert.Equal(2, preview.Items.Count);
                Assert.All(preview.Items, item => Assert.True(item.CanInstall));

                var result = await installer.InstallAsync(
                    new SkillImportInstallRequest
                    {
                        SessionId = preview.SessionId,
                        ItemIds = preview.Items.Select(item => item.ItemId).ToList()
                    },
                    CancellationToken.None);

                Assert.Equal(2, result.Items.Count(item => item.Success));
                var store = new FileSystemSkillStore(roots.BuiltInRoot, roots.UserRoot);
                var skills = await store.GetSkillsAsync(CancellationToken.None);
                Assert.All(skills, skill =>
                {
                    Assert.Equal(SkillTrustLevel.External, skill.TrustLevel);
                    Assert.Equal(SkillScriptPolicy.Disabled, skill.ScriptPolicy);
                });
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public async Task InstallAsync_LocalFolderChangedAfterPreview_RejectsWithoutCreatingTarget()
        {
            var roots = CreateTempRoots();
            try
            {
                var source = Path.Combine(roots.SourceRoot, "changed-skill");
                WriteSkill(roots.SourceRoot, "changed-skill", "可变 Skill");
                var installer = new FileSystemSkillPackageInstaller(roots.BuiltInRoot, roots.UserRoot);
                var preview = await installer.PreviewFoldersAsync(new[] { source }, CancellationToken.None);
                File.AppendAllText(Path.Combine(source, "SKILL.md"), "\n变更内容");

                var result = await installer.InstallAsync(
                    new SkillImportInstallRequest
                    {
                        SessionId = preview.SessionId,
                        ItemIds = new[] { preview.Items[0].ItemId }
                    },
                    CancellationToken.None);

                Assert.False(result.Items.Single().Success);
                Assert.False(Directory.Exists(Path.Combine(roots.UserRoot, "changed-skill")));
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public async Task PreviewFoldersAsync_InvalidFolder_ReturnsItemErrorWithoutThrowingBatch()
        {
            var roots = CreateTempRoots();
            try
            {
                var installer = new FileSystemSkillPackageInstaller(roots.BuiltInRoot, roots.UserRoot);
                var preview = await installer.PreviewFoldersAsync(
                    new[] { Path.Combine(roots.SourceRoot, "missing") },
                    CancellationToken.None);

                Assert.Single(preview.Items);
                Assert.False(preview.Items[0].CanInstall);
                Assert.NotEmpty(preview.Items[0].Errors);
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public async Task PreviewNetworkAsync_PrivateAddress_RejectsBeforeDownload()
        {
            var roots = CreateTempRoots();
            try
            {
                var installer = new FileSystemSkillPackageInstaller(roots.BuiltInRoot, roots.UserRoot);
                var preview = await installer.PreviewNetworkAsync(
                    "https://127.0.0.1/skill.zip",
                    CancellationToken.None);

                Assert.Single(preview.Items);
                Assert.False(preview.Items[0].CanInstall);
                Assert.Contains("公网", preview.Items[0].Errors.Single());
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public void ExtractZipForTests_PathTraversal_RejectsEntry()
        {
            var roots = CreateTempRoots();
            try
            {
                var zipPath = Path.Combine(roots.Root, "unsafe.zip");
                Directory.CreateDirectory(roots.Root);
                using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("../outside.txt");
                    using (var writer = new StreamWriter(entry.Open()))
                    {
                        writer.Write("unsafe");
                    }
                }

                Assert.Throws<InvalidOperationException>(() =>
                    FileSystemSkillPackageInstaller.ExtractZipForTests(
                        zipPath,
                        Path.Combine(roots.Root, "extract"),
                        CancellationToken.None));
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public void ExtractZipForTests_AbsolutePath_RejectsEntry()
        {
            var roots = CreateTempRoots();
            try
            {
                var zipPath = Path.Combine(roots.Root, "absolute.zip");
                Directory.CreateDirectory(roots.Root);
                using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("/outside.txt");
                    using (var writer = new StreamWriter(entry.Open()))
                    {
                        writer.Write("unsafe");
                    }
                }

                Assert.Throws<InvalidOperationException>(() =>
                    FileSystemSkillPackageInstaller.ExtractZipForTests(
                        zipPath,
                        Path.Combine(roots.Root, "extract"),
                        CancellationToken.None));
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        [Fact]
        public void Constructor_CleansExpiredInstallStagingDirectory()
        {
            var roots = CreateTempRoots();
            try
            {
                Directory.CreateDirectory(roots.UserRoot);
                var stagingPath = Path.Combine(roots.UserRoot, ".installing-stale");
                Directory.CreateDirectory(stagingPath);
                Directory.SetLastWriteTimeUtc(stagingPath, DateTime.UtcNow.AddHours(-25));

                _ = new FileSystemSkillPackageInstaller(roots.BuiltInRoot, roots.UserRoot);

                Assert.False(Directory.Exists(stagingPath));
            }
            finally
            {
                DeleteTempRoots(roots);
            }
        }

        private static TempRoots CreateTempRoots()
        {
            var root = Path.Combine(Path.GetTempPath(), "smartword-import-" + Guid.NewGuid().ToString("N"));
            return new TempRoots
            {
                Root = root,
                SourceRoot = Path.Combine(root, "source"),
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
description: 用于测试的外部 Skill。
version: 1.0.0
enabled: true
trust_level: built_in
source: forged
script_policy: prompt
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

            public string SourceRoot { get; set; }

            public string BuiltInRoot { get; set; }

            public string UserRoot { get; set; }
        }
    }
}
