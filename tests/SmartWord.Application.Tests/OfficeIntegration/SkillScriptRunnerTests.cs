using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.SkillScripts;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public sealed class SkillScriptRunnerTests
    {
        [Fact]
        public async Task RunAsync_CSharpScript_WritesJsonResultAndOutput()
        {
            var root = CreateTempRoot();
            try
            {
                var scriptPath = Path.Combine(root, "scripts", "analyze.csx");
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                File.WriteAllText(
                    scriptPath,
                    "Write(\"started\"); WriteOutputText(\"summary.txt\", \"ok\"); WriteJsonResult(new { status = \"ok\" });");
                var runner = new SkillScriptRunner();

                var result = await runner.RunAsync(CreateRequest(root, scriptPath, "csharp"), CancellationToken.None);

                Assert.True(result.Success);
                Assert.Contains("started", result.Stdout);
                Assert.Contains("\"status\":\"ok\"", result.ResultJson);
                Assert.Contains(result.Outputs, item => item.RelativePath == "summary.txt");
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Fact]
        public async Task RunAsync_CSharpScriptUsesSystemIo_ReturnsSecurityError()
        {
            var root = CreateTempRoot();
            try
            {
                var scriptPath = Path.Combine(root, "scripts", "bad.csx");
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                File.WriteAllText(scriptPath, "System.IO.File.ReadAllText(\"x\");");
                var runner = new SkillScriptRunner();

                var result = await runner.RunAsync(CreateRequest(root, scriptPath, "csharp"), CancellationToken.None);

                Assert.False(result.Success);
                Assert.Contains("受限", result.Stderr);
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Fact]
        public async Task RunAsync_PythonImportsSocket_ReturnsSecurityErrorBeforeInterpreterLookup()
        {
            var root = CreateTempRoot();
            try
            {
                var scriptPath = Path.Combine(root, "scripts", "bad.py");
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                File.WriteAllText(scriptPath, "import socket\nprint('bad')");
                var runner = new SkillScriptRunner();

                var result = await runner.RunAsync(CreateRequest(root, scriptPath, "python"), CancellationToken.None);

                Assert.False(result.Success);
                Assert.Contains("socket", result.Stderr);
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Fact]
        public async Task RunAsync_OutOfProcessHostMissing_ReturnsStructuredFailure()
        {
            var hostPath = Path.Combine(Path.GetTempPath(), "smartword-missing-host-" + Guid.NewGuid().ToString("N") + ".exe");
            var runner = new OutOfProcessSkillScriptRunner(hostPath);

            var result = await runner.RunAsync(new SkillScriptRunRequest(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(-10, result.ExitCode);
            Assert.Contains("SmartWord.SkillHost.exe", result.Stderr);
        }

        [Fact]
        public async Task RunAsync_OutOfProcessCSharpScript_ReturnsHostResult()
        {
            var root = CreateTempRoot();
            try
            {
                var scriptPath = Path.Combine(root, "scripts", "host-roundtrip.csx");
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                File.WriteAllText(scriptPath, "WriteJsonResult(new { status = \"host-ok\" });");
                var hostPath = Path.Combine(AppContext.BaseDirectory, "SmartWord.SkillHost.exe");
                var runner = new OutOfProcessSkillScriptRunner(hostPath);

                var result = await runner.RunAsync(CreateRequest(root, scriptPath, "csharp"), CancellationToken.None);

                Assert.True(result.Success, result.Stderr);
                Assert.Contains("host-ok", result.ResultJson);
                Assert.Equal(string.Empty, result.WorkspacePath);
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        private static SkillScriptRunRequest CreateRequest(string root, string scriptPath, string runtime)
        {
            return new SkillScriptRunRequest
            {
                SkillName = "test-skill",
                ScriptPath = "scripts/" + Path.GetFileName(scriptPath),
                Runtime = runtime,
                ArgumentsJson = "{}",
                Purpose = "测试脚本执行。",
                Resolution = new SkillScriptResolution
                {
                    AbsolutePath = scriptPath,
                    Skill = new SkillDefinition
                    {
                        Name = "test-skill",
                        RootPath = root
                    },
                    Script = new SkillScriptInfo
                    {
                        SkillName = "test-skill",
                        RelativePath = "scripts/" + Path.GetFileName(scriptPath),
                        Runtime = runtime,
                        SizeBytes = new FileInfo(scriptPath).Length,
                        Sha256 = "test"
                    }
                }
            };
        }

        private static string CreateTempRoot()
        {
            return Path.Combine(Path.GetTempPath(), "smartword-runner-" + Guid.NewGuid().ToString("N"));
        }

        private static void DeleteTempRoot(string root)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
