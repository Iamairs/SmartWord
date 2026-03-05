// 文件说明：
// 定义待执行动作的业务类别。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 会话动作类型。
    /// </summary>
    public enum ConversationActionType
    {
        /// <summary>
        /// 无动作。
        /// </summary>
        None = 0,

        /// <summary>
        /// 文本改写动作。
        /// </summary>
        Rewrite = 1,

        /// <summary>
        /// VBA 执行动作。
        /// </summary>
        Vba = 2,

        /// <summary>
        /// 混合动作（改写与 VBA 组合）。
        /// </summary>
        Hybrid = 3
    }
}
