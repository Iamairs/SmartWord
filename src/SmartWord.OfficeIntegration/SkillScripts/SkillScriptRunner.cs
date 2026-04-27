using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.OfficeIntegration.SkillScripts
{
    /// <summary>
    /// Skill 脚本执行器：为每次运行创建 workspace，并仅向脚本暴露受控输入输出 API。
    /// </summary>
    public sealed class SkillScriptRunner : ISkillScriptRunner
    {
        private const int MaxScriptBytes = 256 * 1024;
        private const int MaxStreamCharacters = 64 * 1024;
        private const int MaxOutputPreviewCharacters = 4096;
        private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);

        public async Task<SkillScriptRunResult> RunAsync(
            SkillScriptRunRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            ValidateResolvedRequest(request);
            var warnings = new List<string>
            {
                "默认禁止联网；首版采用静态扫描和环境收敛，不是内核级网络沙箱。"
            };

            var workspacePath = CreateWorkspacePath();
            Directory.CreateDirectory(workspacePath);
            Directory.CreateDirectory(Path.Combine(workspacePath, "inputs"));
            Directory.CreateDirectory(Path.Combine(workspacePath, "outputs"));

            CopyConfirmedInputs(request.ConfirmedInputPaths, workspacePath, warnings, cancellationToken);
            var stopwatch = Stopwatch.StartNew();
            SkillScriptRunResult result;
            if (string.Equals(request.Runtime, "csharp", StringComparison.OrdinalIgnoreCase))
            {
                result = await RunCSharpAsync(request, workspacePath, warnings, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (string.Equals(request.Runtime, "python", StringComparison.OrdinalIgnoreCase))
            {
                result = await RunPythonAsync(request, workspacePath, warnings, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException("runtime 仅支持 csharp 或 python。");
            }

            stopwatch.Stop();
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.WorkspacePath = workspacePath;
            result.Warnings = warnings;
            result.Outputs = CollectOutputs(workspacePath, cancellationToken);
            return result;
        }

        private static async Task<SkillScriptRunResult> RunCSharpAsync(
            SkillScriptRunRequest request,
            string workspacePath,
            List<string> warnings,
            CancellationToken cancellationToken)
        {
            var code = ReadScriptText(request.Resolution.AbsolutePath);
            var validation = ValidateCSharpScript(code);
            if (!validation.IsValid)
            {
                return new SkillScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    Stderr = validation.Message,
                    Warnings = warnings
                };
            }

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(ScriptTimeout);
                var globals = new SkillScriptGlobals(
                    request.ArgumentsJson,
                    workspacePath);
                try
                {
                    var options = ScriptOptions.Default
                        .AddReferences(
                            typeof(object).Assembly,
                            typeof(Enumerable).Assembly,
                            typeof(JObject).Assembly,
                            typeof(SkillScriptGlobals).Assembly)
                        .AddImports(
                            "System",
                            "System.Linq",
                            "System.Collections.Generic",
                            "Newtonsoft.Json.Linq");

                    var state = await CSharpScript.RunAsync(
                            code,
                            options,
                            globals,
                            typeof(SkillScriptGlobals),
                            timeoutCts.Token)
                        .ConfigureAwait(false);

                    return new SkillScriptRunResult
                    {
                        Success = true,
                        ExitCode = 0,
                        Stdout = Truncate(globals.GetStdout(), MaxStreamCharacters),
                        Stderr = string.Empty,
                        ResultJson = string.IsNullOrWhiteSpace(globals.ResultJson)
                            ? SerializeReturnValue(state.ReturnValue)
                            : globals.ResultJson
                    };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new SkillScriptRunResult
                    {
                        Success = false,
                        ExitCode = -2,
                        Stderr = "脚本执行超时。"
                    };
                }
                catch (Exception ex)
                {
                    return new SkillScriptRunResult
                    {
                        Success = false,
                        ExitCode = -1,
                        Stderr = Truncate(ex.Message, MaxStreamCharacters),
                        Stdout = Truncate(globals.GetStdout(), MaxStreamCharacters),
                        ResultJson = globals.ResultJson
                    };
                }
            }
        }

        private static async Task<SkillScriptRunResult> RunPythonAsync(
            SkillScriptRunRequest request,
            string workspacePath,
            List<string> warnings,
            CancellationToken cancellationToken)
        {
            var code = ReadScriptText(request.Resolution.AbsolutePath);
            var validation = ValidatePythonScript(code);
            if (!validation.IsValid)
            {
                return new SkillScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    Stderr = validation.Message,
                    Warnings = warnings
                };
            }

            var pythonPath = FindPythonOnPath();
            if (string.IsNullOrWhiteSpace(pythonPath))
            {
                return new SkillScriptRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    Stderr = "未在 PATH 中找到 python.exe。请安装 Python，或把 python.exe 加入 PATH 后重试。"
                };
            }

            var scriptCopyPath = Path.Combine(workspacePath, "script.py");
            File.Copy(request.Resolution.AbsolutePath, scriptCopyPath, true);
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = Quote(scriptCopyPath) + " --args " + Quote(request.ArgumentsJson ?? "{}"),
                WorkingDirectory = workspacePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ConfigureMinimalEnvironment(psi, workspacePath);

            using (var process = new Process { StartInfo = psi })
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(ScriptTimeout);
                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                var exitTask = Task.Run(() =>
                {
                    process.WaitForExit();
                    return process.ExitCode;
                }, timeoutCts.Token);

                try
                {
                    var exitCode = await exitTask.ConfigureAwait(false);
                    var stdout = await stdoutTask.ConfigureAwait(false);
                    var stderr = await stderrTask.ConfigureAwait(false);
                    var resultJson = ReadPythonResultJson(workspacePath);
                    return new SkillScriptRunResult
                    {
                        Success = exitCode == 0,
                        ExitCode = exitCode,
                        Stdout = Truncate(stdout, MaxStreamCharacters),
                        Stderr = Truncate(stderr, MaxStreamCharacters),
                        ResultJson = resultJson
                    };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    return new SkillScriptRunResult
                    {
                        Success = false,
                        ExitCode = -2,
                        Stderr = "脚本执行超时。"
                    };
                }
            }
        }

        private static void ValidateResolvedRequest(SkillScriptRunRequest request)
        {
            if (request.Resolution == null || string.IsNullOrWhiteSpace(request.Resolution.AbsolutePath))
            {
                throw new InvalidOperationException("脚本尚未通过 SkillStore 解析。");
            }

            var fileInfo = new FileInfo(request.Resolution.AbsolutePath);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("未找到指定脚本。", request.Resolution.AbsolutePath);
            }

            if (fileInfo.Length > MaxScriptBytes)
            {
                throw new InvalidOperationException("脚本超过 256KB，已拒绝执行。");
            }
        }

        private static ValidationResult ValidateCSharpScript(string code)
        {
            var forbidden = new[]
            {
                "System.IO",
                "System.Net",
                "HttpClient",
                "WebClient",
                "Socket",
                "System.Diagnostics",
                "Process",
                "Microsoft.Win32",
                "Registry",
                "Microsoft.Office",
                "Interop.Word",
                "WordApp",
                "ActiveDoc",
                "Environment.GetEnvironmentVariable",
                "Environment.SetEnvironmentVariable",
                "AppDomain",
                "Assembly",
                "File.",
                "Directory."
            };

            return ValidateForbiddenText(code, forbidden, "C#");
        }

        private static ValidationResult ValidatePythonScript(string code)
        {
            var forbidden = new[]
            {
                "import socket",
                "from socket",
                "import requests",
                "from requests",
                "import urllib",
                "from urllib",
                "import http.client",
                "from http.client",
                "import subprocess",
                "from subprocess",
                "os.system",
                "popen(",
                "import ctypes",
                "from ctypes",
                "win32com",
                "comtypes",
                "import winreg",
                "from winreg",
                "environ",
                "getenv("
            };

            return ValidateForbiddenText(code, forbidden, "Python");
        }

        private static ValidationResult ValidateForbiddenText(
            string code,
            IEnumerable<string> forbidden,
            string runtime)
        {
            var source = code ?? string.Empty;
            foreach (var item in forbidden)
            {
                if (source.IndexOf(item, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ValidationResult.Invalid(runtime + " 脚本包含受限调用或模块：" + item);
                }
            }

            return ValidationResult.Valid();
        }

        private static void CopyConfirmedInputs(
            IReadOnlyList<string> inputPaths,
            string workspacePath,
            List<string> warnings,
            CancellationToken cancellationToken)
        {
            var inputsRoot = Path.Combine(workspacePath, "inputs");
            var index = 0;
            foreach (var inputPath in inputPaths ?? new List<string>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(inputPath))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(inputPath);
                if (File.Exists(fullPath))
                {
                    File.Copy(fullPath, Path.Combine(inputsRoot, index + "_" + Path.GetFileName(fullPath)), true);
                    index++;
                }
                else if (Directory.Exists(fullPath))
                {
                    var targetRoot = Path.Combine(inputsRoot, index + "_" + Path.GetFileName(fullPath));
                    CopyDirectory(fullPath, targetRoot, cancellationToken);
                    index++;
                }
                else
                {
                    warnings.Add("已跳过不存在的确认输入路径：" + inputPath);
                }
            }
        }

        private static void CopyDirectory(
            string sourceRoot,
            string targetRoot,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(targetRoot);
            foreach (var directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory.Replace(sourceRoot, targetRoot));
            }

            foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = file.Replace(sourceRoot, targetRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static IReadOnlyList<SkillScriptOutputFile> CollectOutputs(
            string workspacePath,
            CancellationToken cancellationToken)
        {
            var outputsRoot = Path.Combine(workspacePath, "outputs");
            if (!Directory.Exists(outputsRoot))
            {
                return new List<SkillScriptOutputFile>();
            }

            var outputs = new List<SkillScriptOutputFile>();
            foreach (var filePath in Directory.GetFiles(outputsRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = filePath
                    .Substring(outputsRoot.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                outputs.Add(new SkillScriptOutputFile
                {
                    RelativePath = relativePath,
                    SizeBytes = new FileInfo(filePath).Length,
                    Sha256 = ComputeSha256(filePath),
                    Preview = ReadPreview(filePath)
                });
            }

            return outputs.OrderBy(item => item.RelativePath).ToList();
        }

        private static string FindPythonOnPath()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                var candidate = Path.Combine(directory.Trim(), "python.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static void ConfigureMinimalEnvironment(ProcessStartInfo psi, string workspacePath)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var temp = Path.Combine(workspacePath, "tmp");
            Directory.CreateDirectory(temp);
            psi.EnvironmentVariables.Clear();
            psi.EnvironmentVariables["PATH"] = path;
            psi.EnvironmentVariables["TEMP"] = temp;
            psi.EnvironmentVariables["TMP"] = temp;
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["SMARTWORD_WORKSPACE"] = workspacePath;
        }

        private static string ReadPythonResultJson(string workspacePath)
        {
            var resultPath = Path.Combine(workspacePath, "outputs", "result.json");
            return File.Exists(resultPath)
                ? File.ReadAllText(resultPath, Encoding.UTF8)
                : string.Empty;
        }

        private static string ReadScriptText(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static string ReadPreview(string filePath)
        {
            try
            {
                var text = File.ReadAllText(filePath, Encoding.UTF8);
                return Truncate(text, MaxOutputPreviewCharacters);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SerializeReturnValue(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return JsonConvert.SerializeObject(value);
        }

        private static string CreateWorkspacePath()
        {
            return Path.Combine(
                Path.GetTempPath(),
                "SmartWord",
                "skill-script-workspaces",
                DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string Truncate(string value, int maxCharacters)
        {
            var text = value ?? string.Empty;
            return text.Length <= maxCharacters
                ? text
                : text.Substring(0, maxCharacters) + Environment.NewLine + "[已截断]";
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

        private static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
            }
        }
    }

    public sealed class SkillScriptGlobals
    {
        private readonly StringBuilder _stdout = new StringBuilder();
        private readonly string _workspacePath;

        public SkillScriptGlobals(string argumentsJson, string workspacePath)
        {
            ArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
            _workspacePath = workspacePath;
            WorkspacePath = workspacePath;
        }

        public string ArgumentsJson { get; }

        public string WorkspacePath { get; }

        public string ResultJson { get; private set; } = string.Empty;

        public string ReadInputText(string relativePath)
        {
            var path = ResolveInside(Path.Combine(_workspacePath, "inputs"), relativePath);
            return File.ReadAllText(path, Encoding.UTF8);
        }

        public void WriteOutputText(string relativePath, string content)
        {
            var path = ResolveInside(Path.Combine(_workspacePath, "outputs"), relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content ?? string.Empty, Encoding.UTF8);
        }

        public void WriteJsonResult(object value)
        {
            ResultJson = value is string text
                ? text
                : JsonConvert.SerializeObject(value);
            WriteOutputText("result.json", ResultJson);
        }

        public void Write(string text)
        {
            _stdout.AppendLine(text ?? string.Empty);
        }

        public string GetStdout()
        {
            return _stdout.ToString();
        }

        private static string ResolveInside(string root, string relativePath)
        {
            var normalizedRoot = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath ?? string.Empty));
            if (!candidate.StartsWith(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("脚本路径越界，已拒绝访问。");
            }

            return candidate;
        }
    }

    internal sealed class ValidationResult
    {
        public bool IsValid { get; set; }

        public string Message { get; set; } = string.Empty;

        public static ValidationResult Valid()
        {
            return new ValidationResult { IsValid = true, Message = "ok" };
        }

        public static ValidationResult Invalid(string message)
        {
            return new ValidationResult { IsValid = false, Message = message ?? string.Empty };
        }
    }
}
