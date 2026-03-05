// 文件说明：
// 定义文本改写请求模型，承载改写流程所需输入参数。
namespace SmartWord.Core.Models
{
    /// <summary>
    /// 编辑改写请求。
    /// </summary>
    public sealed class EditorRewriteRequest
    {
        /// <summary>
        /// 用户自然语言指令。
        /// </summary>
        public string Instruction { get; set; }

        /// <summary>
        /// 当前选中文本。
        /// </summary>
        public string SelectedText { get; set; }

        /// <summary>
        /// 模型覆盖项；为空时由实现层选择默认模型。
        /// </summary>
        public string ModelOverride { get; set; }

        /// <summary>
        /// Prompt 版本标识。
        /// </summary>
        public string PromptVersion { get; set; }
    }
}
