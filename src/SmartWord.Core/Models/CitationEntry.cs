namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示回复中一个可跳转的溯源引用。
    /// </summary>
    public sealed class CitationEntry
    {
        public int Ref { get; set; }

        public int ParagraphIndex { get; set; }

        public string Excerpt { get; set; } = string.Empty;

        public string DocumentPath { get; set; } = string.Empty;
    }
}
