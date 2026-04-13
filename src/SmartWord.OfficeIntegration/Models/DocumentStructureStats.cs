namespace SmartWord.OfficeIntegration.Models
{
    /// <summary>
    /// 表示文档中的结构元素统计。
    /// </summary>
    public sealed class DocumentStructureStats
    {
        public int TableCount { get; set; }

        public int ImageCount { get; set; }

        public int AnnotationCount { get; set; }
    }
}
