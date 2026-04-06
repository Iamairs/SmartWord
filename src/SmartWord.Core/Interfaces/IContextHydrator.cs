using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 负责在任务开始前组装文档上下文快照。
    /// </summary>
    public interface IContextHydrator
    {
        Task<DocumentContext> HydrateAsync(CancellationToken cancellationToken);
    }
}
