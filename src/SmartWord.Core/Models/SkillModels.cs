using System;
using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 描述一个可用于 Word 文档工作流的 Skill 能力包。
    /// </summary>
    public sealed class SkillDefinition
    {
        public string Name { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        public bool IsBuiltIn { get; set; }

        public string RootPath { get; set; } = string.Empty;

        public string SkillFilePath { get; set; } = string.Empty;

        public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Skill 目录中的资源文件。脚本资源需通过 skill_run_script 受控执行。
    /// </summary>
    public sealed class SkillResource
    {
        public string RelativePath { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public long SizeBytes { get; set; }
    }

    /// <summary>
    /// Skill scripts 目录下允许被受控执行的脚本文件。
    /// </summary>
    public sealed class SkillScriptInfo
    {
        public string SkillName { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public string Runtime { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public string Sha256 { get; set; } = string.Empty;

        public bool IsApproved { get; set; }
    }

    /// <summary>
    /// 经过 SkillStore 校验后的脚本解析结果。
    /// </summary>
    public sealed class SkillScriptResolution
    {
        public SkillDefinition Skill { get; set; } = new SkillDefinition();

        public SkillScriptInfo Script { get; set; } = new SkillScriptInfo();

        public string AbsolutePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// 前端查看和编辑 Skill 时使用的完整详情。
    /// </summary>
    public sealed class SkillDetail
    {
        public SkillDefinition Definition { get; set; } = new SkillDefinition();

        public string Content { get; set; } = string.Empty;

        public IReadOnlyList<SkillResource> Resources { get; set; } = new List<SkillResource>();

        public IReadOnlyList<SkillScriptInfo> Scripts { get; set; } = new List<SkillScriptInfo>();
    }

    /// <summary>
    /// 创建 Skill 的请求。正文为空时由 Store 生成安全模板。
    /// </summary>
    public sealed class CreateSkillRequest
    {
        public string Name { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// 保存用户 Skill 内容时的请求。
    /// </summary>
    public sealed class SaveSkillRequest
    {
        public string Name { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// Agent prompt 注入 Skill 时使用的压缩上下文。
    /// </summary>
    public sealed class SkillPromptContext
    {
        public IReadOnlyList<SkillDefinition> AvailableSkills { get; set; } = new List<SkillDefinition>();

        public IReadOnlyList<SkillDetail> ActiveSkills { get; set; } = new List<SkillDetail>();

        public string PromptBlock { get; set; } = string.Empty;
    }

    /// <summary>
    /// skill_run_script 工具输入的规范化运行请求。
    /// </summary>
    public sealed class SkillScriptRunRequest
    {
        public string SkillName { get; set; } = string.Empty;

        public string ScriptPath { get; set; } = string.Empty;

        public string Runtime { get; set; } = string.Empty;

        public string ArgumentsJson { get; set; } = "{}";

        public IReadOnlyList<string> ConfirmedInputPaths { get; set; } = new List<string>();

        public IReadOnlyList<string> ExpectedOutputs { get; set; } = new List<string>();

        public string Purpose { get; set; } = string.Empty;

        public SkillScriptResolution Resolution { get; set; } = new SkillScriptResolution();
    }

    public sealed class SkillScriptOutputFile
    {
        public string RelativePath { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public string Sha256 { get; set; } = string.Empty;

        public string Preview { get; set; } = string.Empty;
    }

    public sealed class SkillScriptRunResult
    {
        public bool Success { get; set; }

        public string Stdout { get; set; } = string.Empty;

        public string Stderr { get; set; } = string.Empty;

        public int ExitCode { get; set; }

        public long DurationMs { get; set; }

        public IReadOnlyList<SkillScriptOutputFile> Outputs { get; set; } = new List<SkillScriptOutputFile>();

        public string ResultJson { get; set; } = string.Empty;

        public IReadOnlyList<string> Warnings { get; set; } = new List<string>();

        public string WorkspacePath { get; set; } = string.Empty;
    }

    public sealed class SkillScriptApprovalKey
    {
        public string SkillName { get; set; } = string.Empty;

        public string RelativeScriptPath { get; set; } = string.Empty;

        public string ScriptHash { get; set; } = string.Empty;

        public string Runtime { get; set; } = string.Empty;

        public string PermissionSet { get; set; } = string.Empty;

        public string ToStableKey()
        {
            return string.Join(
                "|",
                (SkillName ?? string.Empty).Trim().ToLowerInvariant(),
                (RelativeScriptPath ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant(),
                (ScriptHash ?? string.Empty).Trim().ToLowerInvariant(),
                (Runtime ?? string.Empty).Trim().ToLowerInvariant(),
                PermissionSet ?? string.Empty);
        }
    }

    public sealed class SkillScriptApprovalRecord
    {
        public SkillScriptApprovalKey Key { get; set; } = new SkillScriptApprovalKey();

        public DateTimeOffset ApprovedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        public string Purpose { get; set; } = string.Empty;
    }

    public sealed class ToolConfirmationRequest
    {
        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public string ToolInput { get; set; } = string.Empty;

        public string OperationDescription { get; set; } = string.Empty;

        public SkillScriptApprovalKey ScriptApprovalKey { get; set; }
    }

    public sealed class ToolConfirmationDecision
    {
        public bool Confirmed { get; set; }

        public bool Remember { get; set; }

        public static ToolConfirmationDecision FromBoolean(bool confirmed)
        {
            return new ToolConfirmationDecision
            {
                Confirmed = confirmed,
                Remember = false
            };
        }
    }
}
