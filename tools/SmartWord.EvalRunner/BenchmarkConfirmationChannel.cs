using System;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.EvalRunner
{
    internal sealed class BenchmarkConfirmationChannel : IToolConfirmationChannel
    {
        private readonly string _policy;

        public BenchmarkConfirmationChannel(string policy)
        {
            _policy = string.IsNullOrWhiteSpace(policy) ? "approve_required" : policy;
        }

        public bool IsAvailable => true;

        public Task<bool> WaitForConfirmationAsync(string toolCallId, CancellationToken cancellationToken)
        {
            return Task.FromResult(!string.Equals(_policy, "reject_all", StringComparison.OrdinalIgnoreCase));
        }

        public Task<ToolConfirmationDecision> WaitForConfirmationDecisionAsync(
            ToolConfirmationRequest request,
            CancellationToken cancellationToken)
        {
            var confirmed = !string.Equals(_policy, "reject_all", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new ToolConfirmationDecision
            {
                Confirmed = confirmed,
                Remember = false
            });
        }
    }
}
