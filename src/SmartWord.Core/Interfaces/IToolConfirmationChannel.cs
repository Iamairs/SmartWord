using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 扩展确认通道，支持携带确认上下文和“记住授权”决策。
    /// </summary>
    public interface IToolConfirmationChannel : IConfirmationChannel
    {
        Task<ToolConfirmationDecision> WaitForConfirmationDecisionAsync(
            ToolConfirmationRequest request,
            CancellationToken cancellationToken);
    }
}
