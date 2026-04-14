using System.Collections.Generic;
using System.Linq;

namespace SmartWord.OfficeIntegration.Scripting
{
    /// <summary>
    /// 暴露给脚本的全局变量。
    /// </summary>
    public class ScriptGlobals
    {
        public object WordApp { get; set; }

        public object ActiveDoc { get; set; }

        public ScriptContext Context { get; set; } = new ScriptContext();

        private List<string> OutputLines { get; } = new List<string>();

        public void Write(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                OutputLines.Add(message);
            }
        }

        public string GetOutput()
        {
            return string.Join(System.Environment.NewLine, OutputLines.Where(item => !string.IsNullOrWhiteSpace(item)));
        }
    }
}
