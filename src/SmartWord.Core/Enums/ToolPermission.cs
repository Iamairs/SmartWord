namespace SmartWord.Core.Enums
{
    /// <summary>
    /// 表示工具所需的权限级别。
    /// </summary>
    public enum ToolPermission
    {
        ReadOnly = 0,
        StateWrite = 1,
        DocumentPatchWrite = 2,
        ScriptWrite = 3,
        LocalAutomation = 4,

        /// <summary>
        /// 兼容旧测试与旧工具声明，默认按文档补丁写入处理。
        /// </summary>
        Write = DocumentPatchWrite
    }
}
