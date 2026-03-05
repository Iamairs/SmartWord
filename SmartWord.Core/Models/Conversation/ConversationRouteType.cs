// 文件说明：
// 定义会话指令分流的路由类型。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 会话路由类型。
    /// </summary>
    public enum ConversationRouteType
    {
        /// <summary>
        /// 走文本改写链路。
        /// </summary>
        Rewrite = 0,

        /// <summary>
        /// 走 VBA 链路。
        /// </summary>
        Vba = 1,

        /// <summary>
        /// 走混合链路。
        /// </summary>
        Hybrid = 2
    }
}
