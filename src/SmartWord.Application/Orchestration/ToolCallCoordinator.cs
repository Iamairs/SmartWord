using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartWord.Application.Tools;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// 负责工具调用执行前的解析、权限判定和 Skill 脚本审批准备。
    /// </summary>
    internal sealed class ToolCallCoordinator
    {
        private const int ToolErrorMessageMaxLength = 500;

        private readonly IToolRegistry _toolRegistry;
        private readonly PermissionGuard _permissionGuard;
        private readonly ISkillScriptApprovalStore _skillScriptApprovalStore;

        internal ToolCallCoordinator(
            IToolRegistry toolRegistry,
            PermissionGuard permissionGuard,
            ISkillScriptApprovalStore skillScriptApprovalStore)
        {
            _toolRegistry = toolRegistry;
            _permissionGuard = permissionGuard;
            _skillScriptApprovalStore = skillScriptApprovalStore;
        }

        internal async Task<ToolCallPreparation> PrepareAsync(
            ToolCall toolCall,
            AgentRunOptions options,
            CancellationToken cancellationToken)
        {
            JObject parsedInput = null;
            ToolCallResult inputParseError = null;
            try
            {
                parsedInput = string.IsNullOrWhiteSpace(toolCall?.Input)
                    ? new JObject()
                    : JObject.Parse(toolCall.Input);
            }
            catch (Exception ex)
            {
                inputParseError = ToolCallResult.Error(
                    toolCall?.Name ?? string.Empty,
                    AgentOrchestratorUtilities.Truncate(ex.Message, ToolErrorMessageMaxLength));
            }

            var tool = _toolRegistry.GetTool(toolCall?.Name ?? string.Empty);
            var permissionDecision = _permissionGuard.Decide(
                toolCall?.Name ?? string.Empty,
                options.Mode,
                AgentOrchestratorUtilities.ResolvePermissionMode(options));
            var requiresConfirmation = permissionDecision.RequiresConfirmation;
            var eventToolInput = toolCall?.Input ?? string.Empty;
            SkillScriptApprovalKey scriptApprovalKey = null;

            if (permissionDecision.IsAllowed
                && string.Equals(toolCall?.Name, "skill_run_script", StringComparison.OrdinalIgnoreCase)
                && tool is SkillRunScriptTool skillRunScriptTool)
            {
                try
                {
                    EnsureActiveSkillAccess(parsedInput, options, "script_path");
                    scriptApprovalKey = await skillRunScriptTool
                        .BuildApprovalKeyAsync(parsedInput, cancellationToken)
                        .ConfigureAwait(false);
                    var approved = _skillScriptApprovalStore != null
                        && await _skillScriptApprovalStore
                            .IsApprovedAsync(scriptApprovalKey, cancellationToken)
                            .ConfigureAwait(false);
                    requiresConfirmation = !approved;
                    var confirmationInput = await skillRunScriptTool
                        .BuildConfirmationInputAsync(parsedInput, cancellationToken)
                        .ConfigureAwait(false);
                    eventToolInput = confirmationInput.ToString(Formatting.None);
                }
                catch (Exception ex)
                {
                    requiresConfirmation = false;
                    permissionDecision = PermissionDecision.Deny("Skill 脚本解析失败：" + ex.Message);
                }
            }
            else if (permissionDecision.IsAllowed
                && string.Equals(toolCall?.Name, "read_skill_resource", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    EnsureActiveSkillAccess(parsedInput, options, "resource_path");
                }
                catch (Exception ex)
                {
                    permissionDecision = PermissionDecision.Deny("Skill 资源访问被拒绝：" + ex.Message);
                }
            }

            return new ToolCallPreparation
            {
                Tool = tool,
                ParsedInput = parsedInput,
                InputParseError = inputParseError,
                OperationDescription = BuildOperationDescription(toolCall?.Name, parsedInput),
                EventToolInput = eventToolInput,
                PermissionDecision = permissionDecision,
                RequiresConfirmation = requiresConfirmation,
                ScriptApprovalKey = scriptApprovalKey
            };
        }

        internal async Task RememberApprovalAsync(
            SkillScriptApprovalKey approvalKey,
            string purpose,
            CancellationToken cancellationToken)
        {
            if (approvalKey == null || _skillScriptApprovalStore == null)
            {
                return;
            }

            await _skillScriptApprovalStore
                .ApproveAsync(approvalKey, purpose ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }

        private static string BuildOperationDescription(string toolName, JObject parsedInput)
        {
            if (parsedInput != null)
            {
                var description = parsedInput.Value<string>("description");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }

                var operation = parsedInput.Value<string>("operation");
                var purpose = parsedInput.Value<string>("purpose");
                if (!string.IsNullOrWhiteSpace(purpose)
                    && string.Equals(toolName, "skill_run_script", StringComparison.OrdinalIgnoreCase))
                {
                    return "准备执行 Skill 脚本：" + purpose.Trim();
                }

                if (!string.IsNullOrWhiteSpace(operation))
                {
                    switch ((toolName ?? string.Empty).Trim().ToLowerInvariant())
                    {
                        case "patch_range":
                            return "准备执行范围写入：" + operation.Trim();
                        case "read_script":
                            return "准备执行脚本查询：" + operation.Trim();
                        case "verify_script":
                            return "准备验证改动结果：" + operation.Trim();
                        case "execute_script":
                            return "准备执行脚本写入：" + operation.Trim();
                        case "skill_run_script":
                            return "准备执行 Skill 脚本：" + operation.Trim();
                    }
                }

                if (parsedInput.TryGetValue("operations", out var operationsToken)
                    && operationsToken is JArray operationsArray)
                {
                    return "准备执行范围写入，共 " + operationsArray.Count + " 个操作。";
                }
            }

            switch ((toolName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "patch_range":
                    return "准备执行文档范围写入。";
                case "read_script":
                    return "准备执行脚本查询。";
                case "verify_script":
                    return "准备验证本次改动结果。";
                case "execute_script":
                    return "准备执行脚本写入。";
                case "skill_run_script":
                    return "准备执行 Skill 脚本。";
                case "read_skill_resource":
                    return "准备读取当前任务的 Skill 资源。";
                default:
                    return "准备执行工具：" + (toolName ?? string.Empty);
            }
        }

        private static void EnsureActiveSkillAccess(
            JObject input,
            AgentRunOptions options,
            string pathField)
        {
            if (input == null)
            {
                throw new InvalidOperationException("工具输入不能为空。");
            }

            var skillName = (input.Value<string>("skill_name") ?? string.Empty).Trim();
            var requestedPath = (input.Value<string>(pathField) ?? string.Empty)
                .Trim()
                .Replace('\\', '/');
            var snapshot = (options?.ActiveSkillSnapshots ?? new List<ActiveSkillSnapshot>())
                .FirstOrDefault(item => string.Equals(item.Name, skillName, StringComparison.OrdinalIgnoreCase));
            if (snapshot == null)
            {
                throw new InvalidOperationException("Skill 未在当前任务中激活。");
            }

            var allowedPaths = string.Equals(pathField, "script_path", StringComparison.OrdinalIgnoreCase)
                ? snapshot.AllowedScriptPaths
                : snapshot.AllowedResourcePaths;
            if (!(allowedPaths ?? new List<string>()).Any(item => string.Equals(
                item?.Replace('\\', '/'),
                requestedPath,
                StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("请求路径不在当前任务的 Skill 快照白名单中。");
            }
        }
    }

    /// <summary>
    /// 工具调用进入确认和执行阶段前的不可变准备结果。
    /// </summary>
    internal sealed class ToolCallPreparation
    {
        internal ITool Tool { get; set; }

        internal JObject ParsedInput { get; set; }

        internal ToolCallResult InputParseError { get; set; }

        internal string OperationDescription { get; set; } = string.Empty;

        internal string EventToolInput { get; set; } = string.Empty;

        internal PermissionDecision PermissionDecision { get; set; }

        internal bool RequiresConfirmation { get; set; }

        internal SkillScriptApprovalKey ScriptApprovalKey { get; set; }
    }
}
