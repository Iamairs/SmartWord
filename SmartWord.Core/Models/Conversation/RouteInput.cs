// 文件说明：
// 定义路由判定输入模型，聚合用户消息、选区与检索上下文。
namespace SmartWord.Core.Models.Conversation
{
    /// <summary>
    /// 路由输入。
    /// </summary>
    public sealed class RouteInput
    {
        /// <summary>
        /// 用户消息文本。
        /// </summary>
        public string UserMessage { get; set; }

        /// <summary>
        /// 当前选中文本。
        /// </summary>
        public string SelectedText { get; set; }

        /// <summary>
        /// 检索增强上下文文本。
        /// </summary>
        public string RetrievedContext { get; set; }

        /// <summary>
        /// 模型覆盖项。
        /// </summary>
        public string ModelOverride { get; set; }
    }
}
