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

        /// <summary>
        /// 初始化 VBA 模块管理器。
        /// </summary>
        /// <param name="wordApplication">Word 应用实例。</param>
        public VbaModuleManager(dynamic wordApplication)
        {
            _wordApplication = wordApplication;
        }

        /// <summary>
        /// 创建临时 VBA 模块并注入代码。
        /// </summary>
        /// <param name="vbaCode">VBA 代码文本。</param>
        /// <returns>临时模块名称。</returns>
        public string CreateTemporaryModule(string vbaCode)
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
        /// 安全移除指定 VBA 模块。
        /// </summary>
        /// <param name="moduleName">模块名称。</param>
        public void RemoveModuleIfExists(string moduleName)
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
    }
}
