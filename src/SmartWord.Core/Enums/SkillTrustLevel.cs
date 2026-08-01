namespace SmartWord.Core.Enums
{
    /// <summary>
    /// Skill 来源信任级别。信任级别只影响脚本策略，不授予 Word 写入权限。
    /// </summary>
    public enum SkillTrustLevel
    {
        BuiltIn = 0,
        User = 1,
        External = 2
    }
}
