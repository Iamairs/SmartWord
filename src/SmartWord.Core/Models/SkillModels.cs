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
    /// Skill 目录中的资源文件。首版仅展示和提示，不执行 scripts。
    /// </summary>
    public sealed class SkillResource
    {
        public string RelativePath { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;

        public long SizeBytes { get; set; }
    }

    /// <summary>
    /// 前端查看和编辑 Skill 时使用的完整详情。
    /// </summary>
    public sealed class SkillDetail
    {
        public SkillDefinition Definition { get; set; } = new SkillDefinition();

        public string Content { get; set; } = string.Empty;

        public IReadOnlyList<SkillResource> Resources { get; set; } = new List<SkillResource>();
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
}
