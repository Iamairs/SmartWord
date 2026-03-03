using System.Threading.Tasks;

namespace SmartWord.Core.Orchestration
{
    public interface IVbaAgentOrchestrator
    {
        Task RunFormattingAsync(string instruction, string modelOverride, string promptVersion);
    }
}
