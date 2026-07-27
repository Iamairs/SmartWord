using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    internal sealed class AlwaysConfirmChannel : IConfirmationChannel
    {
        public bool IsAvailable => true;

        public Task<bool> WaitForConfirmationAsync(string toolCallId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }
}
