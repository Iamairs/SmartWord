namespace SmartWord.OfficeIntegration.Models
{
    /// <summary>
    /// 表示单条批注的只读结果。
    /// </summary>
    public sealed class AnnotationSnapshot
    {
        public int AnnotationIndex { get; set; }

        public string Author { get; set; } = string.Empty;

        public string CreatedAt { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public string AnchorText { get; set; } = string.Empty;

        public int ParagraphIndex { get; set; } = -1;
    }
}
