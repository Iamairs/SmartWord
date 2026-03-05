using System.Threading.Tasks;

namespace SmartWord.Core.Abstractions.Conversation
{
    public interface IEmbeddingService
    {
        Task<float[]> CreateEmbeddingAsync(string input, string modelOverride);
    }
}
