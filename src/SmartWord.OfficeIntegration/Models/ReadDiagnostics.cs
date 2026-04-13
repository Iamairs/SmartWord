using System.Collections.Generic;

namespace SmartWord.OfficeIntegration.Models
{
    /// <summary>
    /// 表示工具读取过程中的轻量诊断信息。
    /// </summary>
    public sealed class ReadDiagnostics
    {
        public bool IsPartial { get; set; }

        public IList<string> Warnings { get; } = new List<string>();

        public bool HasWarnings => Warnings.Count > 0;

        public void AddWarning(string warning)
        {
            if (!string.IsNullOrWhiteSpace(warning))
            {
                Warnings.Add(warning);
            }
        }
    }
}
