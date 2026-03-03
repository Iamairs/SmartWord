using System.Threading.Tasks;

namespace SmartWord.Core.Orchestration
{
    public interface IEditorAgentOrchestrator
    {
        Task RunRewriteAsync(string instruction, string modelOverride, string promptVersion);
    }
}
