using SmartWord.Core.Models.Conversation;
using System.Threading.Tasks;

namespace SmartWord.Core.Abstractions.Conversation
{
    public interface IDocumentRetriever
    {
        Task<RetrievedContext> RetrieveAsync(DocumentQuery query);
    }
}
