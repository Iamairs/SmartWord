namespace SmartWord.OfficeIntegration.Reading
{
    /// <summary>
    /// 表示按标题、段落、光标或选区解析读取范围的输入参数。
    /// </summary>
    public sealed class ReadScope
    {
        public string Heading { get; set; } = string.Empty;

        public bool IncludeSubsections { get; set; } = true;

        public int? FromParagraph { get; set; }

        public int? ToParagraph { get; set; }

        public bool AroundCursor { get; set; }

        public int ContextWindow { get; set; } = 5;

        public bool SelectionOnly { get; set; }
    }

    /// <summary>
    /// 表示解析完成后的读取范围。
    /// </summary>
    public sealed class ResolvedReadScope
    {
        public int FromParagraph { get; set; }

        public int ToParagraph { get; set; }

        public string HeadingText { get; set; } = string.Empty;
    }
}
