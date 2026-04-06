namespace SmartWord.OfficeIntegration.Scripting
{
    /// <summary>
    /// 暴露给脚本的全局变量。
    /// </summary>
    public class ScriptGlobals
    {
        public object WordApp { get; set; }

        public object ActiveDoc { get; set; }
    }
}
