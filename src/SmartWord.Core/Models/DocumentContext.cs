using System.Collections.Generic;

namespace SmartWord.Core.Models
{
    /// <summary>
    /// 描述当前文档在某一时刻的上下文快照。
    /// </summary>
    public sealed class DocumentContext
    {
        public string DocumentName { get; set; } = string.Empty;

        public string DocumentPath { get; set; } = string.Empty;

        public int WordCount { get; set; }

        public int ParagraphCount { get; set; }

        public int CurrentPageNumber { get; set; }

        public int TotalPages { get; set; }

        public string Complexity { get; set; } = string.Empty;

        public int CursorParagraphIndex { get; set; }

        public int SelectionParagraphIndex { get; set; } = -1;

        public bool HasSelection { get; set; }

        public string SelectedText { get; set; } = string.Empty;

        public int TableCount { get; set; }

        public int ImageCount { get; set; }

        public int AnnotationCount { get; set; }

        public IList<DocumentHeading> Headings { get; set; } = new List<DocumentHeading>();

        public DocumentStatus DocumentStatus { get; set; } = new DocumentStatus();
    }
}
