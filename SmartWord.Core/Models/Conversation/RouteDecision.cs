// 文件说明：
// 定义路由判定结果模型，包含路由类型、置信度与判定原因。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 路由决策结果。
    /// </summary>
    public sealed class RouteDecision
    {
        /// <summary>
        /// 判定出的路由类型。
        /// </summary>
        public ConversationRouteType RouteType { get; set; }

        /// <summary>
        /// 判定置信度。
        /// </summary>
        public double Confidence { get; set; }

        /// <summary>
        /// 判定原因说明。
        /// </summary>
        public string Reason { get; set; }
    }
}
