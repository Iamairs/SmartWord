using System;

namespace SmartWord.Services.Vba
{
    /// VBA模块管理器
    /// 负责在Word文档中动态创建、管理和清理临时VBA模块
    /// 这是SmartWord核心功能的重要组成部分，支持动态代码注入和执行
    internal sealed class VbaModuleManager
    {
        /// VBA标准模块类型常量
        /// 值为1，表示创建标准VBA模块（与类模块、窗体模块等区分）
        private const int VbextCtStdModule = 1;
        
        /// Word应用程序实例的动态引用
        /// 使用dynamic类型以支持COM互操作，避免早期绑定的版本依赖问题
        private readonly dynamic _wordApplication;

        /// 初始化VBA模块管理器
        /// Word应用程序实例，用于访问VBA项目和组件
        public VbaModuleManager(dynamic wordApplication)
        {
            _wordApplication = wordApplication;
        }

        /// 创建临时VBA模块并注入代码
        public string CreateTemporaryModule(string vbaCode)
        {
            // 获取当前活动文档的VBA项目
            dynamic vbProject = _wordApplication.ActiveDocument.VBProject;
            // 获取VBA组件集合，用于添加新模块
            dynamic vbComponents = vbProject.VBComponents;
            // 创建标准VBA模块
            dynamic module = vbComponents.Add(VbextCtStdModule);

            // 生成唯一的临时模块名称，使用GUID确保不与现有模块冲突
            string moduleName = "SmartWordTemp_" + Guid.NewGuid().ToString("N").Substring(0, 8);


            // 设置模块名称并注入VBA代码
            module.Name = moduleName;
            module.CodeModule.AddFromString(vbaCode);
            
            return moduleName;
        }

        /// 安全移除指定的VBA模块
        /// 在代码执行完毕后清理临时模块，避免污染Word文档的VBA项目
        public void RemoveModuleIfExists(string moduleName)
        {
            // 参数验证：空值或空白字符串直接返回
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return;
            }

            // 获取VBA项目组件集合
            dynamic vbProject = _wordApplication.ActiveDocument.VBProject;
            dynamic vbComponents = vbProject.VBComponents;

            try
            {
                // 尝试获取指定名称的模块
                dynamic module = vbComponents.Item(moduleName);
                if (module != null)
                {
                    // 移除模块
                    vbComponents.Remove(module);
                }
            }
            catch
            {
                // 忽略清理过程中的错误，避免掩盖原始异常
                // 这种设计确保即使清理失败，也不会影响主要的业务逻辑流程
            }
        }
    }
}
