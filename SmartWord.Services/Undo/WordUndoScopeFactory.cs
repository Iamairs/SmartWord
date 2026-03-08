using System;
using SmartWord.Core.Abstractions;

// 文件说明：
// Word 撤销作用域工厂，实现按能力探测创建可用撤销范围并在失败时降级。
namespace SmartWord.Services.Undo
{
    /// <summary>
    /// Word 撤销作用域工厂。
    /// </summary>
    public sealed class WordUndoScopeFactory : IUndoScopeFactory
    {
        private readonly dynamic _wordApplication;
        private readonly INotificationService _notificationService;
        private readonly IWordThreadInvoker _wordThreadInvoker;

        /// <summary>
        /// 初始化撤销作用域工厂。
        /// </summary>
        /// <param name="wordApplication">Word 应用实例。</param>
        /// <param name="notificationService">通知服务。</param>
        /// <param name="wordThreadInvoker">Word 主线程调用器。</param>
        public WordUndoScopeFactory(dynamic wordApplication, INotificationService notificationService, IWordThreadInvoker wordThreadInvoker)
        {
            _wordApplication = wordApplication;
            _notificationService = notificationService;
            _wordThreadInvoker = wordThreadInvoker;
        }

        /// <summary>
        /// 创建具名撤销作用域。
        /// </summary>
        /// <param name="name">撤销项名称。</param>
        /// <returns>可用撤销作用域；失败时返回空作用域。</returns>
        public IUndoScope Begin(string name)
        {
            return InvokeOnWordThread(() => BeginCore(name));
        }

        /// <summary>
        /// 在 Word 主线程创建撤销作用域。
        /// </summary>
        /// <param name="name">撤销项名称。</param>
        /// <returns>撤销作用域。</returns>
        private IUndoScope BeginCore(string name)
        {
            if (_wordApplication == null)
            {
                return NoopUndoScope.Instance;
            }

            try
            {
                dynamic undoRecord = _wordApplication.UndoRecord;
                if (undoRecord == null)
                {
                    // 某些 Word 环境未启用 UndoRecord，降级但不中断流程。
                    _notificationService?.Info("UndoRecord is not available. Running without grouped undo.");
                    return NoopUndoScope.Instance;
                }

                undoRecord.StartCustomRecord(name);
                return new WordUndoScope(undoRecord);
            }
            catch (Exception)
            {
                // COM 失败时降级为无分组撤销，保证主流程可继续。
                _notificationService?.Info("Failed to start UndoRecord. Running without grouped undo.");
                return NoopUndoScope.Instance;
            }
        }

        /// <summary>
        /// 在 Word 主线程执行带返回值逻辑。
        /// </summary>
        /// <typeparam name="T">返回值类型。</typeparam>
        /// <param name="func">待执行逻辑。</param>
        /// <returns>执行结果。</returns>
        private T InvokeOnWordThread<T>(Func<T> func)
        {
            if (func == null)
            {
                return default(T);
            }

            if (_wordThreadInvoker == null)
            {
                return func();
            }

            return _wordThreadInvoker.Invoke(func);
        }
    }
}
