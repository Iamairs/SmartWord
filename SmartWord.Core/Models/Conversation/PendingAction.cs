using System;

// 文件说明：
// 定义待执行动作模型，用于承载确认前的候选改写或脚本执行计划。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 待执行动作。
    /// </summary>
    public sealed class PendingAction
    {
        /// <summary>
        /// 动作唯一标识。
        /// </summary>
        public string ActionId { get; set; }

        /// <summary>
        /// 动作类别。
        /// </summary>
        public ConversationActionType ActionType { get; set; }

        /// <summary>
        /// 产生该动作的路由类型。
        /// </summary>
        public ConversationRouteType RouteType { get; set; }

        /// <summary>
        /// 待应用改写文本。
        /// </summary>
        public string RewriteText { get; set; }

        /// <summary>
        /// 待执行 VBA 代码。
        /// </summary>
        public string VbaCode { get; set; }

        /// <summary>
        /// VBA 入口过程名称。
        /// </summary>
        public string EntryPoint { get; set; }

        /// <summary>
        /// 是否已应用。
        /// </summary>
        public bool IsApplied { get; set; }

        /// <summary>
        /// 创建时间（UTC）。
        /// </summary>
        public DateTime CreatedAtUtc { get; set; }
    }
}
