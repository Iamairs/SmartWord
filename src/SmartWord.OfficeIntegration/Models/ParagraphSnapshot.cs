using System.Collections.Generic;

namespace SmartWord.OfficeIntegration.Models
{
    /// <summary>
    /// 表示只读快照中的单个段落。
    /// </summary>
    public sealed class ParagraphSnapshot
    {
        public int Index { get; set; }

        public string Style { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public int Start { get; set; }

        public int End { get; set; }

        public IList<TextMatchSnapshot> Matches { get; set; } = new List<TextMatchSnapshot>();
    }
}
