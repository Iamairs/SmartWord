namespace SmartWord.OfficeIntegration.Models
{
    /// <summary>
    /// 表示段落在 Word Range 中的起止位置。
    /// </summary>
    public sealed class ParagraphRangeBounds
    {
        public int Index { get; set; }

        public int Start { get; set; }

        public int End { get; set; }
    }
}
