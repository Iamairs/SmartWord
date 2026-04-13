using System.Collections.Generic;
using SmartWord.Core.Models;

namespace SmartWord.OfficeIntegration.Models
{
    /// <summary>
    /// 表示单次只读工具执行期间共享的文档快照。
    /// </summary>
    public sealed class DocumentReadSnapshot
    {
        public string DocumentPath { get; set; } = string.Empty;

        public string DocumentName { get; set; } = string.Empty;

        public DocumentStatus Status { get; set; } = new DocumentStatus();

        public int ParagraphCount { get; set; }

        public int WordCount { get; set; }

        public int CurrentPage { get; set; }

        public int TotalPages { get; set; }

        public string Complexity { get; set; } = string.Empty;

        public int CursorParagraphIndex { get; set; } = -1;

        public SelectionSnapshot Selection { get; set; } = new SelectionSnapshot();

        public DocumentStructureStats Stats { get; set; } = new DocumentStructureStats();

        public IReadOnlyList<DocumentHeading> Headings { get; set; } = new List<DocumentHeading>();
    }
}
