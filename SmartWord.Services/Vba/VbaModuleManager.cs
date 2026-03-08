using SmartWord.Core.Abstractions;
using System;

// 文件说明：
// VBA 模块管理器，负责在 Word 文档中创建临时模块并在执行后安全清理。
namespace SmartWord.Services.Vba
{
    /// <summary>
    /// VBA 模块管理器。
    /// </summary>
    internal sealed class VbaModuleManager
    {
        /// <summary>
        /// VBA 标准模块类型常量（1 代表标准模块）。
        /// </summary>
        private const int VbextCtStdModule = 1;

        /// <summary>
        /// Word 应用程序动态引用（COM 互操作）。
        /// </summary>
        private readonly dynamic _wordApplication;
        private readonly IWordThreadInvoker _wordThreadInvoker;

        /// <summary>
        /// 初始化 VBA 模块管理器。
        /// </summary>
        /// <param name="wordApplication">Word 应用实例。</param>
        /// <param name="wordThreadInvoker">Word 主线程调用器。</param>
        public VbaModuleManager(dynamic wordApplication, IWordThreadInvoker wordThreadInvoker)
        {
            _wordApplication = wordApplication;
            _wordThreadInvoker = wordThreadInvoker;
        }

        /// <summary>
        /// 创建临时 VBA 模块并注入代码。
        /// </summary>
        /// <param name="vbaCode">VBA 代码文本。</param>
        /// <returns>临时模块名称。</returns>
        public string CreateTemporaryModule(string vbaCode)
        {
            return InvokeOnWordThread(() => CreateTemporaryModuleCore(vbaCode));
        }

        /// <summary>
        /// 安全移除指定 VBA 模块。
        /// </summary>
        /// <param name="moduleName">模块名称。</param>
        public void RemoveModuleIfExists(string moduleName)
        {
            InvokeOnWordThread(() => RemoveModuleIfExistsCore(moduleName));
        }

        /// <summary>
        /// 在 Word 主线程创建临时模块并注入代码。
        /// </summary>
        /// <param name="vbaCode">VBA 代码文本。</param>
        /// <returns>临时模块名称。</returns>
        private string CreateTemporaryModuleCore(string vbaCode)
        {
            // 获取当前活动文档的 VBA 项目。
            dynamic vbProject = _wordApplication.ActiveDocument.VBProject;
            // 获取 VBA 组件集合，用于添加新模块。
            dynamic vbComponents = vbProject.VBComponents;
            // 创建标准 VBA 模块。
            dynamic module = vbComponents.Add(VbextCtStdModule);

            // 生成唯一临时模块名，避免与现有模块冲突。
            string moduleName = "SmartWordTemp_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // 设置模块名称并注入 VBA 代码。
            module.Name = moduleName;
            module.CodeModule.AddFromString(vbaCode);

            return moduleName;
        }

        /// <summary>
        /// 在 Word 主线程安全移除指定 VBA 模块。
        /// </summary>
        /// <param name="moduleName">模块名称。</param>
        private void RemoveModuleIfExistsCore(string moduleName)
        {
            // 参数为空时直接返回。
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return;
            }

            // 获取 VBA 项目组件集合。
            dynamic vbProject = _wordApplication.ActiveDocument.VBProject;
            dynamic vbComponents = vbProject.VBComponents;

            try
            {
                // 尝试获取指定模块并删除。
                dynamic module = vbComponents.Item(moduleName);
                if (module != null)
                {
                    vbComponents.Remove(module);
                }
            }
            catch
            {
                // 忽略清理异常，避免掩盖主流程异常。
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
