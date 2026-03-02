using SmartWord.Core.Models;

namespace SmartWord.Core.Abstractions
{
    public interface IModelService
    {
        string RewriteText(EditorRewriteRequest request);

        string GenerateVbaCode(VbaGenerationRequest request);
    }
}
