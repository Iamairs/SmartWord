using System.Threading.Tasks;

// 文件说明：
// 定义编辑改写编排入口，供 UI 层以统一方式触发文本改写流程。
namespace SmartWord.Core.Orchestration
{
    /// <summary>
    /// 编辑改写编排器契约。
    /// </summary>
    public interface IEditorAgentOrchestrator
    {
        /// <summary>
        /// 执行改写流程。
        /// </summary>
        /// <param name="instruction">用户自然语言指令。</param>
        /// <param name="modelOverride">模型覆盖项。</param>
        /// <param name="promptVersion">Prompt 版本标识。</param>
        Task RunRewriteAsync(string instruction, string modelOverride, string promptVersion);
    }
}
