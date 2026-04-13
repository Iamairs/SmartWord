using System.Collections.Generic;

namespace SmartWord.OfficeIntegration.Models
{
    /// <summary>
    /// 表示单个表格的只读结果。
    /// </summary>
    public sealed class TableSnapshot
    {
        public int TableIndex { get; set; }

        public int AnchorParagraphIndex { get; set; } = -1;

        public int RowCount { get; set; }

        public int ColumnCount { get; set; }

        public bool RowsTruncated { get; set; }

        public bool ColumnsTruncated { get; set; }

        public IList<TableRowSnapshot> Rows { get; set; } = new List<TableRowSnapshot>();
    }

    /// <summary>
    /// 表示表格中的单行。
    /// </summary>
    public sealed class TableRowSnapshot
    {
        public int RowIndex { get; set; }

        public IList<TableCellSnapshot> Cells { get; set; } = new List<TableCellSnapshot>();
    }

    /// <summary>
    /// 表示表格中的单元格。
    /// </summary>
    public sealed class TableCellSnapshot
    {
        public int ColumnIndex { get; set; }

        public string Text { get; set; } = string.Empty;
    }
}
