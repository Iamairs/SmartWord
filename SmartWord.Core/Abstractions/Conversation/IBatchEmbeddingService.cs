using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// 文件说明：
// 定义批量向量化能力抽象，用于降低检索场景下的网络与序列化开销。
namespace SmartWord.Core.Abstractions.Conversation
{
    /// <summary>
    /// 批量向量化服务契约。
    /// </summary>
    public interface IBatchEmbeddingService : IEmbeddingService
    {
        /// <summary>
        /// 批量生成向量表示。
        /// </summary>
        /// <param name="inputs">输入文本集合。</param>
        /// <param name="modelOverride">模型覆盖项；为空时由实现层选择默认模型。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>向量数组集合，顺序与输入一致。</returns>
        Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(
            IReadOnlyList<string> inputs,
            string modelOverride,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
