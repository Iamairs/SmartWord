using System;
using System.Runtime.InteropServices;
using SmartWord.Core.Abstractions;
using SmartWord.Services.Logging;
using SmartWord.Services.Undo;

// 文件说明：
// VBA 执行器，负责在 Word 中注入临时模块、运行入口过程并清理资源。
namespace SmartWord.Services.Vba
{
    /// <summary>
    /// VBA 执行器。
    /// </summary>
    public sealed class VbaExecutor : IVbaExecutor
    {
        private const string DefaultUndoRecordName = "SmartWord AI Format";
        private readonly dynamic _wordApplication;
        private readonly IUndoScopeFactory _undoScopeFactory;
        private readonly IAppLogger _logger;
        private readonly IWordThreadInvoker _wordThreadInvoker;

        /// <summary>
        /// 初始化 VBA 执行器。
        /// </summary>
        /// <param name="wordApplication">Word 应用实例。</param>
        /// <param name="undoScopeFactory">撤销作用域工厂。</param>
        /// <param name="logger">日志服务。</param>
        /// <param name="wordThreadInvoker">Word 主线程调用器。</param>
        public VbaExecutor(dynamic wordApplication, IUndoScopeFactory undoScopeFactory, IAppLogger logger, IWordThreadInvoker wordThreadInvoker)
        {
            _wordApplication = wordApplication;
            _undoScopeFactory = undoScopeFactory;
            _logger = logger ?? NullAppLogger.Instance;
            _wordThreadInvoker = wordThreadInvoker;
        }

        /// <summary>
        /// 执行 VBA 代码。
        /// </summary>
        /// <param name="vbaCode">VBA 代码。</param>
        /// <param name="entryPoint">入口过程名称。</param>
        /// <exception cref="InvalidOperationException">Word 不可用或 COM 执行失败时抛出。</exception>
        /// <exception cref="ArgumentException">参数非法时抛出。</exception>
        public void Execute(string vbaCode, string entryPoint)
        {
            if (_wordApplication == null)
            {
                throw new InvalidOperationException("Word Application instance cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(vbaCode))
            {
                throw new ArgumentException("VBA code cannot be empty.", nameof(vbaCode));
            }

            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException("Entry point cannot be empty.", nameof(entryPoint));
            }

            InvokeOnWordThread(() => ExecuteOnWordThread(vbaCode, entryPoint));
        }

        /// <summary>
        /// 在 Word 主线程执行完整 VBA 运行闭环。
        /// </summary>
        /// <param name="vbaCode">VBA 代码。</param>
        /// <param name="entryPoint">入口过程名称。</param>
        private void ExecuteOnWordThread(string vbaCode, string entryPoint)
        {
            var moduleManager = new VbaModuleManager(_wordApplication, _wordThreadInvoker);
            string tempModuleName = null;
            IUndoScope undoScope = _undoScopeFactory?.Begin(DefaultUndoRecordName);
            _logger.Info("vba.execute.start", "Executing VBA. EntryPoint={EntryPoint} CodeLength={CodeLength}", entryPoint, vbaCode.Length);

            if (undoScope == null)
            {
                undoScope = NoopUndoScope.Instance;
            }

            using (undoScope)
            {
                try
                {
                    // 注入临时模块后仅执行入口，减少对现有 VBA 项目的影响面。
                    tempModuleName = moduleManager.CreateTemporaryModule(vbaCode);
                    _wordApplication.Run(entryPoint);
                    _logger.Info("vba.execute.end", "VBA executed successfully. EntryPoint={EntryPoint} ModuleName={ModuleName}", entryPoint, tempModuleName);
                }
                catch (COMException ex)
                {
                    _logger.Error("vba.execute.failed", ex, "VBA execution failed. EntryPoint={EntryPoint}", entryPoint);
                    throw new InvalidOperationException("VBA execution failed. Please try rephrasing your request.", ex);
                }
                finally
                {
                    // 无论执行成功与否均尝试回收临时模块，避免污染文档。
                    moduleManager.RemoveModuleIfExists(tempModuleName);
                    _logger.Debug("vba.module.cleanup", "Temporary VBA module cleanup finished. ModuleName={ModuleName}", tempModuleName ?? string.Empty);
                }
            }
        }

        /// <summary>
        /// 在 Word 主线程执行无返回值逻辑。
        /// </summary>
        /// <param name="action">待执行逻辑。</param>
        private void InvokeOnWordThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (_wordThreadInvoker == null)
            {
                action();
                return;
            }

            _wordThreadInvoker.Invoke(action);
        }
    }
}
