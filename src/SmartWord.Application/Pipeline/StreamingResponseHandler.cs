using SmartWord.Core.Models;

namespace SmartWord.Application.Pipeline
{
    /// <summary>
    /// 统一封装流式消息事件的创建逻辑。
    /// </summary>
    public class StreamingResponseHandler
    {
        public AgentEvent CreateStreamChunk(string chunk)
        {
            return new AgentEvent
            {
                Type = AgentEventType.StreamChunk,
                Content = chunk ?? string.Empty
            };
        }
    }
}
