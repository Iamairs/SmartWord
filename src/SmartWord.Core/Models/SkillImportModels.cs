using System;
using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 一次外部 Skill 导入预览。安装时必须携带同一个会话标识。
    /// </summary>
    public sealed class SkillImportPreview
    {
        public string SessionId { get; set; } = string.Empty;

        public DateTimeOffset ExpiresAtUtc { get; set; }

        public IReadOnlyList<SkillImportPreviewItem> Items { get; set; } =
            new List<SkillImportPreviewItem>();
    }

    /// <summary>
    /// 单个网络包或本地目录的校验预览。
    /// </summary>
    public sealed class SkillImportPreviewItem
    {
        public string ItemId { get; set; } = string.Empty;

        public string SourceKind { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string ContentSha256 { get; set; } = string.Empty;

        public long TotalBytes { get; set; }

        public int FileCount { get; set; }

        public int ResourceCount { get; set; }

        public int ScriptCount { get; set; }

        public IReadOnlyList<string> RequiredTools { get; set; } = new List<string>();

        public IReadOnlyList<string> Warnings { get; set; } = new List<string>();

        public IReadOnlyList<string> Errors { get; set; } = new List<string>();

        public bool CanInstall { get; set; }
    }

    /// <summary>
    /// 用户确认安装预览项时提交的请求。
    /// </summary>
    public sealed class SkillImportInstallRequest
    {
        public string SessionId { get; set; } = string.Empty;

        public IReadOnlyList<string> ItemIds { get; set; } = new List<string>();
    }

    /// <summary>
    /// 导入批次的逐项安装结果。
    /// </summary>
    public sealed class SkillImportResult
    {
        public IReadOnlyList<SkillImportResultItem> Items { get; set; } =
            new List<SkillImportResultItem>();
    }

    public sealed class SkillImportResultItem
    {
        public string ItemId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;
    }
}
