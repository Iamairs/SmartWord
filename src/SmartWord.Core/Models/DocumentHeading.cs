namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示文档大纲中的一个标题节点。
    /// </summary>
    public sealed class DocumentHeading
    {
        public int Level { get; set; }

        public string Text { get; set; } = string.Empty;

        public int ParagraphIndex { get; set; }

        public int ChildCount { get; set; }
    }
}
