using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 为支持按轮次强制工具调用的 LLM 客户端提供可选扩展能力。
    /// </summary>
    public interface IToolChoiceLlmClient
    {
        Task<AgentMessage> ChatCompletionWithToolsAsync(
            IReadOnlyList<AgentMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition> tools,
            bool requireToolCall,
            Action<string> onStreamChunk,
            CancellationToken cancellationToken);
    }
}
