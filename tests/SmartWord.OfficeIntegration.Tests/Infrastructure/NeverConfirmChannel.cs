using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    internal sealed class NeverConfirmChannel : IConfirmationChannel
    {
        public bool IsAvailable => true;

        public async Task<bool> WaitForConfirmationAsync(string toolCallId, CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return false;
        }
    }
}
