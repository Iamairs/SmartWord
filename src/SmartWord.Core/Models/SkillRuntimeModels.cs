using System;
using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// Skill 自动推荐结果。推荐结果只提供决策依据，不改变全局启停状态。
    /// </summary>
    public sealed class SkillRecommendation
    {
        public string SkillName { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public double Score { get; set; }

        public string Reason { get; set; } = string.Empty;

        public bool AutoActivated { get; set; }
    }

    /// <summary>
    /// 当前任务使用的 Skill 不可变快照。
    /// </summary>
    public sealed class ActiveSkillSnapshot
    {
        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string ContentSha256 { get; set; } = string.Empty;

        public IReadOnlyList<string> AllowedScriptPaths { get; set; } = new List<string>();

        public IReadOnlyList<string> AllowedResourcePaths { get; set; } = new List<string>();
    }

    /// <summary>
    /// Skill 资源解析结果。路径已经通过 Store 的根目录校验。
    /// </summary>
    public sealed class SkillResourceResolution
    {
        public SkillDefinition Skill { get; set; } = new SkillDefinition();

        public SkillResource Resource { get; set; } = new SkillResource();

        public string AbsolutePath { get; set; } = string.Empty;

        public bool IsText { get; set; }
    }

    /// <summary>
    /// 受控读取 Skill 资源的请求。
    /// </summary>
    public sealed class SkillResourceReadRequest
    {
        public string SkillName { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public string Purpose { get; set; } = string.Empty;

        public ActiveSkillSnapshot ActiveSkill { get; set; }
    }

    /// <summary>
    /// 受控读取 Skill 资源的结果。
    /// </summary>
    public sealed class SkillResourceReadResult
    {
        public bool Success { get; set; }

        public string RelativePath { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public bool Truncated { get; set; }

        public long SizeBytes { get; set; }

        public string Sha256 { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Skill Prompt 加载统计，供前端和本地遥测使用。
    /// </summary>
    public sealed class SkillPromptLoadMetrics
    {
        public int BudgetTokens { get; set; }

        public int EstimatedTokens { get; set; }

        public int IndexTokens { get; set; }

        public IReadOnlyList<string> LoadedSections { get; set; } = new List<string>();
    }
}
