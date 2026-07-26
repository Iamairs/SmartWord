using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Serilog;

namespace SmartWord.OfficeIntegration.WordWrappers
{
    /// <summary>
    /// 将 Word COM 操作调度到创建包装器的宿主 UI 线程，并负责关闭时收尾排队任务。
    /// </summary>
    internal sealed class WordUiDispatcher : IDisposable
    {
        private readonly object _syncRoot = new object();
        private readonly HashSet<IPendingInvocation> _pendingInvocations = new HashSet<IPendingInvocation>();
        private readonly Control _uiThreadInvoker;
        private readonly int _ownerThreadId;
        private readonly bool _useDirectInvoke;
        private bool _disposed;

        public WordUiDispatcher(bool useDirectInvoke)
        {
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            _useDirectInvoke = useDirectInvoke;

            if (!_useDirectInvoke)
            {
                _uiThreadInvoker = new Control();
                var handle = _uiThreadInvoker.Handle;
            }
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (_useDirectInvoke || Thread.CurrentThread.ManagedThreadId == _ownerThreadId)
            {
                lock (_syncRoot)
                {
                    ThrowIfDisposed();
                }

                try
                {
                    return Task.FromResult(action());
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }

            var pendingInvocation = new PendingInvocation<T>();
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                _pendingInvocations.Add(pendingInvocation);
            }

            try
            {
                _uiThreadInvoker.BeginInvoke(new Action(() => ExecutePending(pendingInvocation, action)));
            }
            catch (Exception ex)
            {
                RemovePending(pendingInvocation);
                Log.Warning(ex, "Word UI 调度失败，排队任务将以异常结束。");
                pendingInvocation.Fail(CreateDispatchException(ex));
            }

            return pendingInvocation.Task;
        }

        public void Dispose()
        {
            List<IPendingInvocation> pendingInvocations;
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                pendingInvocations = new List<IPendingInvocation>(_pendingInvocations);
                _pendingInvocations.Clear();
            }

            var disposedException = new ObjectDisposedException(
                nameof(WordUiDispatcher),
                "Word UI 调度器已经关闭，不能继续执行 COM 操作。");
            foreach (var pendingInvocation in pendingInvocations)
            {
                pendingInvocation.Fail(disposedException);
            }

            try
            {
                _uiThreadInvoker?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "关闭 Word UI 调度器时释放 WinForms invoker 失败。");
            }
        }

        private void ExecutePending<T>(PendingInvocation<T> pendingInvocation, Func<T> action)
        {
            lock (_syncRoot)
            {
                if (_disposed || !_pendingInvocations.Remove(pendingInvocation))
                {
                    return;
                }
            }

            try
            {
                pendingInvocation.Complete(action());
            }
            catch (Exception ex)
            {
                pendingInvocation.Fail(ex);
            }
        }

        private void RemovePending(IPendingInvocation pendingInvocation)
        {
            lock (_syncRoot)
            {
                _pendingInvocations.Remove(pendingInvocation);
            }
        }

        private static Exception CreateDispatchException(Exception exception)
        {
            if (exception is ObjectDisposedException)
            {
                return exception;
            }

            return new InvalidOperationException(
                "无法将 Word COM 操作调度到宿主 UI 线程。",
                exception);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(WordUiDispatcher),
                    "Word UI 调度器已经关闭，不能继续执行 COM 操作。");
            }
        }

        private interface IPendingInvocation
        {
            void Fail(Exception exception);
        }

        private sealed class PendingInvocation<T> : IPendingInvocation
        {
            private readonly TaskCompletionSource<T> _completionSource =
                new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<T> Task => _completionSource.Task;

            public void Complete(T result)
            {
                _completionSource.TrySetResult(result);
            }

            public void Fail(Exception exception)
            {
                _completionSource.TrySetException(exception);
            }
        }
    }
}
