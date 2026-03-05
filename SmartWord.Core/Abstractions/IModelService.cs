using SmartWord.Core.Models;
using System.Threading.Tasks;

// 文件说明：
// 定义大语言模型能力的统一抽象，供重写、VBA 生成与通用对话场景复用。
namespace SmartWord.Core.Abstractions
{
    /// <summary>
    /// 模型服务契约。
    /// </summary>
    public interface IModelService
    {
        /// <summary>
        /// 根据用户指令改写选中文本。
        /// </summary>
        /// <param name="request">重写请求参数。</param>
        /// <returns>模型返回的改写结果文本。</returns>
        Task<string> RewriteTextAsync(EditorRewriteRequest request);

        /// <summary>
        /// 根据用户指令生成可执行的 VBA 代码。
        /// </summary>
        /// <param name="request">VBA 生成请求参数。</param>
        /// <returns>模型返回的 VBA 源码字符串。</returns>
        Task<string> GenerateVbaCodeAsync(VbaGenerationRequest request);

        /// <summary>
        /// 通用对话接口，供路由、重排与其他提示词驱动场景复用。
        /// </summary>
        /// <param name="systemPrompt">系统提示词。</param>
        /// <param name="userPrompt">用户提示词。</param>
        /// <param name="modelOverride">模型覆盖项；为空时由实现层决定默认模型。</param>
        /// <param name="temperature">采样温度。</param>
        /// <returns>模型对话响应文本。</returns>
        Task<string> ChatWithPromptsAsync(string systemPrompt, string userPrompt, string modelOverride, double temperature);
    }
}
