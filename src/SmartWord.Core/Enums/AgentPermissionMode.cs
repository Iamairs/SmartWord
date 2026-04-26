namespace SmartWord.Core.Enums
{
    /// <summary>
    /// 表示 Agent 模式下用户选择的执行权限档位。
    /// </summary>
    public enum AgentPermissionMode
    {
        ReadOnly = 0,
        ConfirmWrites = 1,
        AutoSafeWrites = 2,
        FullAuto = 3
    }
}
