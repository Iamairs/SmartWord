using SmartWord.Core.Abstractions.Conversation;
using System;
using System.Threading.Tasks;

namespace SmartWord.Services.Embedding
{
    // 文件说明：
    // 本地向量化降级实现，在远端向量服务不可用时提供可运行的语义检索能力。
    /// <summary>
    /// 本地向量服务。
    /// </summary>
    public sealed class LocalEmbeddingService : IEmbeddingService
    {
        private const int VectorSize = 64;

        /// <summary>
        /// 为输入文本生成本地降级向量。
        /// </summary>
        /// <param name="input">输入文本。</param>
        /// <param name="modelOverride">模型覆盖项（本地实现中不使用）。</param>
        /// <returns>归一化向量。</returns>
        public Task<float[]> CreateEmbeddingAsync(string input, string modelOverride)
        {
            // 本地降级向量：用于离线或 API 不可用时保证检索链路可运行。
            string text = input ?? string.Empty;
            var vector = new float[VectorSize];

            for (int i = 0; i < text.Length; i++)
            {
                int bucket = i % VectorSize;
                vector[bucket] += ((int)text[i] % 31) / 31f;
            }

            float norm = 0f;
            for (int i = 0; i < vector.Length; i++)
            {
                norm += vector[i] * vector[i];
            }

            // 归一化处理，保证不同长度输入的向量可比较。
            norm = (float)Math.Sqrt(norm);
            if (norm > 0f)
            {
                for (int i = 0; i < vector.Length; i++)
                {
                    vector[i] = vector[i] / norm;
                }
            }

            return Task.FromResult(vector);
        }
    }
}
