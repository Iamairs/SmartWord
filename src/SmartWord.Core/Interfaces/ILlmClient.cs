using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 抽象 LLM 流式响应能力，方便后续替换不同提供方。
    /// </summary>
    public interface ILlmClient
    {
        IAsyncEnumerable<string> ChatCompletionStreamAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            CancellationToken cancellationToken);

        Task<AgentMessage> ChatCompletionWithToolsAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition> tools,
            System.Action<string> onStreamChunk,
            CancellationToken cancellationToken);
    }
}
