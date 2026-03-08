using SmartWord.Core.Abstractions;
using System;
using System.Runtime.ExceptionServices;
using System.Threading;

// 文件说明：
// 基于 SynchronizationContext 的 Word 主线程封送实现，避免跨线程访问 COM 对象。
namespace SmartWord.Services.Threading
{
    /// <summary>
    /// Word 线程调用器实现。
    /// </summary>
    public sealed class WordThreadInvoker : IWordThreadInvoker
    {
        private readonly SynchronizationContext _context;
        private readonly int _ownerThreadId;

        /// <summary>
        /// 初始化线程调用器。
        /// </summary>
        /// <param name="context">Word 主线程同步上下文。</param>
        /// <param name="ownerThreadId">Word 主线程 ID。</param>
        public WordThreadInvoker(SynchronizationContext context, int ownerThreadId)
        {
            _context = context;
            _ownerThreadId = ownerThreadId;
        }

        /// <summary>
        /// 在 Word 主线程执行逻辑（无返回值）。
        /// </summary>
        /// <param name="action">待执行逻辑。</param>
        public void Invoke(Action action)
        {
            if (action == null)
            {
                return;
            }

            Invoke<object>(() =>
            {
                action();
                return null;
            });
        }

        /// <summary>
        /// 在 Word 主线程执行逻辑并返回结果。
        /// </summary>
        /// <typeparam name="T">返回值类型。</typeparam>
        /// <param name="func">待执行逻辑。</param>
        /// <returns>执行结果。</returns>
        public T Invoke<T>(Func<T> func)
        {
            if (func == null)
            {
                return default(T);
            }

            // 已在主线程时直接执行，减少不必要的上下文切换。
            if (Environment.CurrentManagedThreadId == _ownerThreadId)
            {
                return func();
            }

            if (_context == null)
            {
                throw new InvalidOperationException("Word synchronization context is unavailable. Cannot marshal COM call to Word main thread.");
            }

            T result = default(T);
            Exception captured = null;
            _context.Send(_ =>
            {
                try
                {
                    result = func();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            }, null);

            if (captured != null)
            {
                ExceptionDispatchInfo.Capture(captured).Throw();
            }

            return result;
        }
    }
}
