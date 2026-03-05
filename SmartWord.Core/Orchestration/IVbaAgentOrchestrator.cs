using System.Threading.Tasks;

// 文件说明：
// 定义 VBA 编排入口，供 UI 层触发格式化或自动化执行链路。
namespace SmartWord.Core.Orchestration
{
    /// <summary>
    /// VBA 编排器契约。
    /// </summary>
    public interface IVbaAgentOrchestrator
    {
        /// <summary>
        /// 执行 VBA 格式化流程。
        /// </summary>
        /// <param name="instruction">用户自然语言指令。</param>
        /// <param name="modelOverride">模型覆盖项。</param>
        /// <param name="promptVersion">Prompt 版本标识。</param>
        Task RunFormattingAsync(string instruction, string modelOverride, string promptVersion);
    }
}
