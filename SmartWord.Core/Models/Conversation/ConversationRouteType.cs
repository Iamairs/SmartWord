// 文件说明：
// 定义会话模式路由类型，兼容历史旧值并提供新的任务导向模式。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 会话路由类型。
    /// </summary>
    public enum ConversationRouteType
    {
        /// <summary>
        /// 旧版改写模式（兼容历史会话）。
        /// </summary>
        Rewrite = 0,

        /// <summary>
        /// 旧版 VBA 模式（兼容历史会话）。
        /// </summary>
        Vba = 1,

        /// <summary>
        /// 旧版混合模式（兼容历史会话）。
        /// </summary>
        Hybrid = 2,

        /// <summary>
        /// 文档问答模式（只读）。
        /// </summary>
        Qa = 3,

        /// <summary>
        /// 写作改写模式（以文本创作为主）。
        /// </summary>
        Writing = 4,

        /// <summary>
        /// 处理模式（结构化整理与规则处理）。
        /// </summary>
        Processing = 5,

        /// <summary>
        /// 执行模式（涉及 VBA 或高执行性动作）。
        /// </summary>
        Execute = 6
    }
}
