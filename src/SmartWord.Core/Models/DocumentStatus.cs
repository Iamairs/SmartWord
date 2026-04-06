namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示文档当前的可写性与保护状态。
    /// </summary>
    public sealed class DocumentStatus
    {
        public bool IsWritable { get; set; } = true;

        public bool IsReadOnly { get; set; }

        public bool IsPasswordProtected { get; set; }

        public bool IsTrackChangesEnforced { get; set; }

        public string Reason { get; set; } = string.Empty;

        public string GetUserFriendlyMessage()
        {
            if (IsWritable)
            {
                return "文档可正常编辑。";
            }

            if (IsPasswordProtected)
            {
                return "文档已加密保护，当前无法直接写入。";
            }

            if (IsReadOnly)
            {
                return "文档处于只读状态，请先解除只读限制。";
            }

            return string.IsNullOrWhiteSpace(Reason) ? "文档当前不可写。" : Reason;
        }
    }
}
