namespace SmartWord.OfficeIntegration.Scripting
{
    /// <summary>
    /// 描述脚本执行所需的最小上下文。
    /// </summary>
    public class ScriptContext
    {
        public string DocumentPath { get; set; } = string.Empty;

        public string SelectionText { get; set; } = string.Empty;
    }
}
