using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;

namespace SmartWord.Application.Tools
{
    /// <summary>
    /// 统一裁决当前模式是否允许执行指定工具。
    /// </summary>
    public sealed class PermissionGuard
    {
        private readonly IToolRegistry _toolRegistry;

        public PermissionGuard(IToolRegistry toolRegistry)
        {
            _toolRegistry = toolRegistry;
        }

        public bool IsAllowed(string toolName, AgentMode mode)
        {
            var tool = _toolRegistry.GetTool(toolName);
            if (tool == null)
            {
                return false;
            }

            if (tool.RequiredPermission == ToolPermission.ReadOnly)
            {
                return true;
            }

            return mode == AgentMode.Agent;
        }
    }
}
