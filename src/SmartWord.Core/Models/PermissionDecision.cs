namespace SmartWord.Core.Models
{
    /// <summary>
    /// 表示一次工具调用的权限裁决结果。
    /// </summary>
    public sealed class PermissionDecision
    {
        public bool IsAllowed { get; set; }

        public bool RequiresConfirmation { get; set; }

        public string Reason { get; set; } = string.Empty;

        public static PermissionDecision Allow(bool requiresConfirmation = false, string reason = "")
        {
            return new PermissionDecision
            {
                IsAllowed = true,
                RequiresConfirmation = requiresConfirmation,
                Reason = reason ?? string.Empty
            };
        }

        public static PermissionDecision Deny(string reason)
        {
            return new PermissionDecision
            {
                IsAllowed = false,
                RequiresConfirmation = false,
                Reason = reason ?? string.Empty
            };
        }
    }
}
