// 文件说明：
// 定义 VBA 代码生成请求模型，承载指令、选区与执行入口信息。
namespace SmartWord.Core.Models
{
    /// <summary>
    /// VBA 生成请求。
    /// </summary>
    public sealed class VbaGenerationRequest
    {
        /// <summary>
        /// 初始化 VBA 生成请求。
        /// </summary>
        public VbaGenerationRequest()
        {
            // 统一默认入口，降低执行器与调用方的约定成本。
            EntryPoint = "SmartWord_Run";
        }

        /// <summary>
        /// 用户自然语言指令。
        /// </summary>
        public string Instruction { get; set; }

        /// <summary>
        /// 当前选中文本。
        /// </summary>
        public string SelectedText { get; set; }

        /// <summary>
        /// VBA 入口过程名称。
        /// </summary>
        public string EntryPoint { get; set; }

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
