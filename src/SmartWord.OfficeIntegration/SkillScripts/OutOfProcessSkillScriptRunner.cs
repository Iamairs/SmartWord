using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.OfficeIntegration.SkillScripts
{
    /// <summary>
    /// 通过一次调用一次进程的 SkillHost 执行脚本，避免脚本进入 Word AddIn 进程。
    /// </summary>
    public sealed class OutOfProcessSkillScriptRunner : ISkillScriptRunner
    {
        private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(35);
        private const int MaxHostResponseCharacters = 8 * 1024 * 1024;
        private readonly string _hostPath;

        public OutOfProcessSkillScriptRunner()
            : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SmartWord.SkillHost.exe"))
        {
        }

        public OutOfProcessSkillScriptRunner(string hostPath)
        {
            _hostPath = Path.GetFullPath(hostPath ?? string.Empty);
        }

        public async Task<SkillScriptRunResult> RunAsync(
            SkillScriptRunRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!File.Exists(_hostPath))
            {
                return Failure(-10, "未找到 SmartWord.SkillHost.exe，已拒绝回退到 Word 进程内执行。");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _hostPath,
                WorkingDirectory = Path.GetDirectoryName(_hostPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
            using (var job = new WindowsJobObject())
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(HostTimeout);
                var exitSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (_, __) => TryCompleteExit(process, exitSource);
                try
                {
                    process.Start();
                    job.AddProcess(process);
                    if (process.HasExited)
                    {
                        TryCompleteExit(process, exitSource);
                    }

                    var requestJson = JsonConvert.SerializeObject(
                        request,
                        Formatting.None,
                        new JsonSerializerSettings
                        {
                            StringEscapeHandling = StringEscapeHandling.EscapeNonAscii
                        });
                    await process.StandardInput.WriteLineAsync(requestJson).ConfigureAwait(false);
                    process.StandardInput.Close();
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var cancellationTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
                    var completed = await Task.WhenAny(exitSource.Task, cancellationTask).ConfigureAwait(false);
                    if (completed != exitSource.Task)
                    {
                        job.Terminate(124);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        return Failure(-2, "SkillHost 执行超时，进程树已终止。");
                    }

                    var exitCode = await exitSource.Task.ConfigureAwait(false);
                    var stdout = await stdoutTask.ConfigureAwait(false);
                    var stderr = await stderrTask.ConfigureAwait(false);
                    if (stdout.Length > MaxHostResponseCharacters)
                    {
                        return Failure(-11, "SkillHost 返回内容超过 8MB，已拒绝处理。");
                    }

                    if (exitCode != 0)
                    {
                        return Failure(exitCode, "SkillHost 异常退出：" + Truncate(stderr, 4096));
                    }

                    try
                    {
                        var response = JsonConvert.DeserializeObject<SkillScriptRunResult>(stdout);
                        return response ?? Failure(-12, "SkillHost 未返回有效结果。");
                    }
                    catch (JsonException ex)
                    {
                        return Failure(-12, "SkillHost 返回 JSON 无效：" + ex.Message + "；摘要：" + Truncate(stdout, 1024));
                    }
                }
                catch (OperationCanceledException)
                {
                    job.Terminate(125);
                    throw;
                }
                catch (Exception ex)
                {
                    job.Terminate(126);
                    return Failure(-13, "SkillHost 调用失败：" + ex.Message);
                }
            }
        }

        private static SkillScriptRunResult Failure(int exitCode, string message)
        {
            return new SkillScriptRunResult
            {
                Success = false,
                ExitCode = exitCode,
                Stderr = message ?? string.Empty
            };
        }

        private static void TryCompleteExit(Process process, TaskCompletionSource<int> source)
        {
            try
            {
                source.TrySetResult(process.ExitCode);
            }
            catch (Exception ex)
            {
                source.TrySetException(ex);
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            var text = value ?? string.Empty;
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }
    }
}
