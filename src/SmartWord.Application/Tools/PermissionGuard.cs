using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

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

        public PermissionDecision Decide(
            string toolName,
            AgentMode mode,
            AgentPermissionMode permissionMode)
        {
            var tool = _toolRegistry.GetTool(toolName);
            if (tool == null)
            {
                return PermissionDecision.Deny("未找到对应的工具实现。");
            }

            if (tool.RequiredPermission == ToolPermission.ReadOnly)
            {
                return PermissionDecision.Allow();
            }

            if (mode == AgentMode.Ask || mode == AgentMode.Plan)
            {
                return PermissionDecision.Deny("当前模式只允许只读工具，不能执行写入或状态变更。");
            }

            if (mode != AgentMode.Agent)
            {
                return PermissionDecision.Deny("当前运行模式不支持该工具。");
            }

            switch (permissionMode)
            {
                case AgentPermissionMode.ReadOnly:
                    return PermissionDecision.Deny("当前处于只读模式，不能执行写入或状态变更。");

                case AgentPermissionMode.ConfirmWrites:
                    return PermissionDecision.Allow(IsUserConfirmedWrite(tool.RequiredPermission));

                case AgentPermissionMode.AutoSafeWrites:
                    return PermissionDecision.Allow(
                        tool.RequiredPermission == ToolPermission.ScriptWrite
                        || tool.RequiredPermission == ToolPermission.LocalAutomation);

                case AgentPermissionMode.FullAuto:
                    return PermissionDecision.Allow();

                default:
                    return PermissionDecision.Deny("当前权限模式无效，系统已拒绝执行该工具。");
            }
        }

        public bool IsAllowed(string toolName, AgentMode mode)
        {
            return Decide(toolName, mode, AgentPermissionMode.ConfirmWrites).IsAllowed;
        }

        private static bool IsUserConfirmedWrite(ToolPermission permission)
        {
            return permission == ToolPermission.DocumentPatchWrite
                || permission == ToolPermission.ScriptWrite
                || permission == ToolPermission.LocalAutomation
                || permission == ToolPermission.Write;
        }
    }
}
