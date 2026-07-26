using System;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;

namespace SmartWord.OfficeIntegration.ComInterop
{
    /// <summary>
    /// 仅为幂等只读 COM 调用提供 Word Busy 有限重试。
    /// </summary>
    internal static class ComBusyRetryPolicy
    {
        internal const int RpcCallRejected = unchecked((int)0x80010001);
        internal const int RpcServerCallRetryLater = unchecked((int)0x8001010A);

        public static T ExecuteRead<T>(
            Func<T> action,
            string operationName,
            int maxAttempts = 3,
            int initialDelayMilliseconds = 40,
            Action<int> wait = null)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (maxAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            }

            wait = wait ?? Thread.Sleep;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return action();
                }
                catch (COMException ex) when (IsWordBusy(ex) && attempt < maxAttempts)
                {
                    var delayMilliseconds = Math.Max(0, initialDelayMilliseconds) * attempt;
                    Log.Warning(
                        ex,
                        "Word COM 暂时忙碌，将重试只读操作。Operation={Operation}, Attempt={Attempt}, MaxAttempts={MaxAttempts}, DelayMs={DelayMs}, HResult={HResult}",
                        operationName ?? string.Empty,
                        attempt,
                        maxAttempts,
                        delayMilliseconds,
                        ex.ErrorCode);
                    wait(delayMilliseconds);
                }
                catch (COMException ex) when (IsWordBusy(ex))
                {
                    Log.Warning(
                        ex,
                        "Word COM 只读操作在有限重试后仍然忙碌。Operation={Operation}, Attempts={Attempts}, HResult={HResult}",
                        operationName ?? string.Empty,
                        maxAttempts,
                        ex.ErrorCode);
                    throw;
                }
            }
        }

        internal static bool IsWordBusy(COMException exception)
        {
            return exception != null
                && (exception.ErrorCode == RpcCallRejected
                    || exception.ErrorCode == RpcServerCallRetryLater);
        }
    }
}
