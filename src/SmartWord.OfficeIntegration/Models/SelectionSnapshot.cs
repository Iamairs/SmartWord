namespace SmartWord.OfficeIntegration.Models
{
    /// <summary>
    /// 表示当前光标或选区的读取快照。
    /// </summary>
    public sealed class SelectionSnapshot
    {
        public bool HasSelection { get; set; }

        public string Text { get; set; } = string.Empty;

        public int ParagraphIndex { get; set; } = -1;

        public int StartParagraphIndex { get; set; } = -1;

        public int EndParagraphIndex { get; set; } = -1;

        public bool IsMultiParagraph { get; set; }

        public int CharStart { get; set; } = -1;

        public int CharEnd { get; set; } = -1;
    }
}
