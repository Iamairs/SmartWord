// 文件说明：
// 定义“应用待执行动作”后的结果模型，供 UI 展示执行状态与反馈信息。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 待执行动作应用结果。
    /// </summary>
    public sealed class ApplyActionResult
    {
        /// <summary>
        /// 所属会话 ID。
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// 动作 ID。
        /// </summary>
        public string ActionId { get; set; }

        /// <summary>
        /// 是否应用成功。
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 执行结果说明。
        /// </summary>
        public string Message { get; set; }
    }
}
