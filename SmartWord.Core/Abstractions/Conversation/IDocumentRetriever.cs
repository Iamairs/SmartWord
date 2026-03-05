using SmartWord.Core.Models.Conversation;
using System.Threading.Tasks;

// 文件说明：
// 定义文档检索能力抽象，用于为对话提供上下文增强。
namespace SmartWord.Core.Abstractions.Conversation
{
    /// <summary>
    /// 文档检索服务契约。
    /// </summary>
    public interface IDocumentRetriever
    {
        /// <summary>
        /// 根据查询条件检索文档上下文。
        /// </summary>
        /// <param name="query">检索查询对象。</param>
        /// <returns>检索到的上下文结果。</returns>
        Task<RetrievedContext> RetrieveAsync(DocumentQuery query);
    }
}
