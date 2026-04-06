using System.Collections.Generic;
using SmartWord.Core.Enums;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 管理工具注册与按模式暴露的工具定义。
    /// </summary>
    public interface IToolRegistry
    {
        void Register(ITool tool);

        IReadOnlyList<ToolDefinition> GetToolDefinitions(AgentMode mode);

        ITool GetTool(string toolName);
    }
}
