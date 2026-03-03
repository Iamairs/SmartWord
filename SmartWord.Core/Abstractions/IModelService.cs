using SmartWord.Core.Models;
using System.Threading.Tasks;

namespace SmartWord.Core.Abstractions
{
    public interface IModelService
    {
        Task<string> RewriteTextAsync(EditorRewriteRequest request);

        Task<string> GenerateVbaCodeAsync(VbaGenerationRequest request);
    }
}
