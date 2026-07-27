using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    internal sealed class InMemoryTodoStore : ITodoStore
    {
        private TodoBoard _board;

        public Task<TodoBoard> GetBoardAsync(string documentPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_board);
        }

        public Task SaveBoardAsync(TodoBoard board, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _board = board;
            return Task.CompletedTask;
        }

        public Task DeleteBoardAsync(string documentPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _board = null;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string documentPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_board != null);
        }
    }
}
