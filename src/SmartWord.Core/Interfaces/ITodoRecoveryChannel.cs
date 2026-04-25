using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 负责在 Todo Board 进入恢复态时等待前端给出恢复决策。
    /// </summary>
    public interface ITodoRecoveryChannel
    {
        bool IsAvailable { get; }

        Task<TodoBoardRecoveryDecision> WaitForDecisionAsync(
            string recoveryRequestId,
            CancellationToken cancellationToken);
    }
}
