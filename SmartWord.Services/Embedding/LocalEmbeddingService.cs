using SmartWord.Core.Abstractions.Conversation;
using System;
using System.Threading.Tasks;

namespace SmartWord.Services.Embedding
{
    public sealed class LocalEmbeddingService : IEmbeddingService
    {
        private const int VectorSize = 64;

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
