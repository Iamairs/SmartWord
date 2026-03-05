using SmartWord.Core.Models;
using System.Threading.Tasks;

namespace SmartWord.Core.Abstractions
{
    public interface IModelService
    {
        Task<string> RewriteTextAsync(EditorRewriteRequest request);

        Task<string> GenerateVbaCodeAsync(VbaGenerationRequest request);

        // 通用对话接口，供路由与重排等能力复用。
        Task<string> ChatWithPromptsAsync(string systemPrompt, string userPrompt, string modelOverride, double temperature);
    }
}
