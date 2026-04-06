using System.Collections.Generic;
using System.Threading;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 定义 Agent 主循环的统一入口。
    /// </summary>
    public interface IAgentOrchestrator
    {
        IAsyncEnumerable<AgentEvent> RunAsync(
            string userInput,
            AgentRunOptions options,
            CancellationToken cancellationToken);
    }
}
