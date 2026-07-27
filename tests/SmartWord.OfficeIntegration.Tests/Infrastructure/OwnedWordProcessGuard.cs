using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    /// <summary>
    /// 只跟踪并清理当前测试创建的 Word 进程，绝不枚举或关闭用户 Word。
    /// </summary>
    internal sealed class OwnedWordProcessGuard
    {
        private const int GracefulExitTimeoutMilliseconds = 5000;
        private const int ForcedExitTimeoutMilliseconds = 3000;

        private OwnedWordProcessGuard(int processId)
        {
            ProcessId = processId;
        }

        public int ProcessId { get; }

        public static HashSet<int> SnapshotWordProcessIds()
        {
            return new HashSet<int>(Process.GetProcessesByName("WINWORD").Select(process =>
            {
                using (process)
                {
                    return process.Id;
                }
            }));
        }

        public static OwnedWordProcessGuard Capture(
            object wordApplication,
            ISet<int> processIdsBeforeStart)
        {
            if (wordApplication == null)
            {
                throw new ArgumentNullException(nameof(wordApplication));
            }

            if (TryCaptureFromWindowHandle(wordApplication, out var processId))
            {
                return new OwnedWordProcessGuard(processId);
            }

            var existingIds = processIdsBeforeStart ?? new HashSet<int>();
            var newProcessIds = SnapshotWordProcessIds()
                .Where(id => !existingIds.Contains(id))
                .ToList();
            if (newProcessIds.Count != 1)
            {
                throw new InvalidOperationException(
                    "无法唯一确定测试 Word 实例的进程 ID，已拒绝继续以避免误清理用户进程。新增 PID 数量="
                    + newProcessIds.Count);
            }

            return new OwnedWordProcessGuard(newProcessIds[0]);
        }

        private static bool TryCaptureFromWindowHandle(object wordApplication, out int processId)
        {
            processId = 0;
            try
            {
                var rawWindowHandle = ((object)wordApplication).GetType().InvokeMember(
                    "Hwnd",
                    BindingFlags.GetProperty,
                    null,
                    (object)wordApplication,
                    null);
                var windowHandle = new IntPtr(Convert.ToInt64(rawWindowHandle));
                GetWindowThreadProcessId(windowHandle, out var nativeProcessId);
                processId = unchecked((int)nativeProcessId);
                return processId > 0;
            }
            catch
            {
                return false;
            }
        }

        public void EnsureExited()
        {
            Process process;
            try
            {
                process = Process.GetProcessById(ProcessId);
            }
            catch (ArgumentException)
            {
                return;
            }

            using (process)
            {
                if (process.WaitForExit(GracefulExitTimeoutMilliseconds))
                {
                    return;
                }

                process.Kill();
                if (!process.WaitForExit(ForcedExitTimeoutMilliseconds))
                {
                    throw new InvalidOperationException("测试拥有的 Word 进程未能退出。PID=" + ProcessId);
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}
