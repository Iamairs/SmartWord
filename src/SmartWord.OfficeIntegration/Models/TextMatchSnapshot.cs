namespace SmartWord.OfficeIntegration.Models
{
    /// <summary>
    /// 表示段落内单次文本命中的位置。
    /// </summary>
    public sealed class TextMatchSnapshot
    {
        public int Start { get; set; }

        public int Length { get; set; }
    }
}
