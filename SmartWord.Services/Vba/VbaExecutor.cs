using System;
using System.Runtime.InteropServices;
using SmartWord.Core.Abstractions;
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

        /// <summary>
        /// 初始化 VBA 执行器。
        /// </summary>
        /// <param name="wordApplication">Word 应用实例。</param>
        /// <param name="undoScopeFactory">撤销作用域工厂。</param>
        public VbaExecutor(dynamic wordApplication, IUndoScopeFactory undoScopeFactory)
        {
            _wordApplication = wordApplication;
            _undoScopeFactory = undoScopeFactory;
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

            var moduleManager = new VbaModuleManager(_wordApplication);
            string tempModuleName = null;
            IUndoScope undoScope = _undoScopeFactory?.Begin(DefaultUndoRecordName);

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
                }
                catch (COMException ex)
                {
                    throw new InvalidOperationException("VBA execution failed. Please try rephrasing your request.", ex);
                }
                finally
                {
                    // 无论执行成功与否均尝试回收临时模块，避免污染文档。
                    moduleManager.RemoveModuleIfExists(tempModuleName);
                }
            }
        }
    }
}
