using System.Collections.Generic;
using System.Linq;

namespace SmartWord.OfficeIntegration.Scripting
{
    /// <summary>
    /// 暴露给脚本的全局变量。
    /// </summary>
    public class ScriptGlobals
    {
        public dynamic WordApp { get; set; }

        public dynamic ActiveDoc { get; set; }

        public dynamic App { get; set; }

        public dynamic Doc { get; set; }

        public dynamic app { get; set; }

        public dynamic doc { get; set; }

        public dynamic wordApp { get; set; }

        public dynamic activeDoc { get; set; }

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
