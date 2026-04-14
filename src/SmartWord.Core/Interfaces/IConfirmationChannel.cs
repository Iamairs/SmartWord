using System.Threading;
using System.Threading.Tasks;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 抽象写前确认通道，供编排层等待前端回传确认结果。
    /// </summary>
    public interface IConfirmationChannel
    {
        bool IsAvailable { get; }

        Task<bool> WaitForConfirmationAsync(string toolCallId, CancellationToken cancellationToken);
    }
}
