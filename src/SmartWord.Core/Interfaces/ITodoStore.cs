using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 抽象 Todo Board 的持久化能力，便于后续切换到 SQLite。
    /// </summary>
    public interface ITodoStore
    {
        Task<TodoBoard> GetBoardAsync(string documentPath, CancellationToken cancellationToken);

        Task SaveBoardAsync(TodoBoard board, CancellationToken cancellationToken);

        Task DeleteBoardAsync(string documentPath, CancellationToken cancellationToken);

        Task<bool> ExistsAsync(string documentPath, CancellationToken cancellationToken);
    }
}
