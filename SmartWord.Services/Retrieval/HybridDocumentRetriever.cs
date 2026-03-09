using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Core.Models.Conversation;
using SmartWord.Services.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// 文件说明：
// 混合检索器实现，融合关键词、向量相似度与可选 LLM 重排，为会话提供高相关上下文。
namespace SmartWord.Services.Retrieval
{
    /// <summary>
    /// 混合文档检索器。
    /// </summary>
    public sealed class HybridDocumentRetriever : IDocumentRetriever
    {
        private readonly WordDocumentChunkProvider _chunkProvider;
        private readonly IEmbeddingService _embeddingService;
        private readonly VectorIndexStore _vectorIndexStore;
        private readonly IModelService _modelService;

        /// <summary>
        /// 初始化混合检索器。
        /// </summary>
        public HybridDocumentRetriever(
            WordDocumentChunkProvider chunkProvider,
            IEmbeddingService embeddingService,
            VectorIndexStore vectorIndexStore,
            IModelService modelService)
        {
            _chunkProvider = chunkProvider;
            _embeddingService = embeddingService;
            _vectorIndexStore = vectorIndexStore;
            _modelService = modelService;
        }

        /// <summary>
        /// 执行检索并返回上下文。
        /// </summary>
        /// <param name="query">检索查询。</param>
        /// <returns>检索上下文。</returns>
        public async Task<RetrievedContext> RetrieveAsync(DocumentQuery query, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = _chunkProvider.CaptureSnapshot();
            var context = new RetrievedContext
            {
                DocumentId = snapshot.DocumentId
            };

            if (snapshot.Chunks.Count == 0)
            {
                context.CombinedText = string.Empty;
                return context;
            }

            int maxChunks = query == null || query.MaxChunks <= 0 ? 5 : query.MaxChunks;
            string queryText = BuildQueryText(query);
            // 第一阶段：关键词召回，覆盖基础相关性。
            List<ScoredChunk> candidates = ScoreByKeyword(snapshot.Chunks, queryText);

            Dictionary<string, float[]> vectorMap = await _vectorIndexStore.GetOrCreateEmbeddingsAsync(
                snapshot.DocumentId,
                snapshot.Chunks,
                _embeddingService,
                query == null ? null : query.ModelOverride);

            cancellationToken.ThrowIfCancellationRequested();

            float[] queryVector = _embeddingService == null
                ? new float[0]
                : await _embeddingService.CreateEmbeddingAsync(queryText, query == null ? null : query.ModelOverride);

            // 第二阶段：融合向量分数，得到混合评分。
            for (int i = 0; i < candidates.Count; i++)
            {
                float[] chunkVector;
                if (!vectorMap.TryGetValue(candidates[i].Chunk.ChunkId, out chunkVector))
                {
                    chunkVector = new float[0];
                }

                double vectorScore = CosineSimilarity(queryVector, chunkVector);
                candidates[i].VectorScore = vectorScore;
                candidates[i].TotalScore = 0.55d * candidates[i].KeywordScore + 0.45d * vectorScore;
            }

            List<ScoredChunk> topCandidates = candidates
                .OrderByDescending(item => item.TotalScore)
                .Take(Math.Max(maxChunks * 2, maxChunks))
                .ToList();

            // 第三阶段：可选 LLM 重排，提升候选精度。
            List<ScoredChunk> reranked = await TryRerankAsync(topCandidates, queryText, query == null ? null : query.ModelOverride, cancellationToken);
            List<ScoredChunk> finalChunks = reranked
                .OrderByDescending(item => item.TotalScore)
                .Take(maxChunks)
                .ToList();

            var combined = new StringBuilder();
            for (int i = 0; i < finalChunks.Count; i++)
            {
                ScoredChunk item = finalChunks[i];
                context.Chunks.Add(new RetrievedChunk
                {
                    ChunkId = item.Chunk.ChunkId,
                    Position = item.Chunk.Position,
                    Text = item.Chunk.Text,
                    Score = item.TotalScore
                });

                if (combined.Length > 0)
                {
                    combined.Append("\n\n");
                }

                combined.Append("[").Append(item.Chunk.Position).Append("] ").Append(item.Chunk.Text);
            }

            context.CombinedText = combined.ToString();
            return context;
        }

        /// <summary>
        /// 尝试使用模型对候选分片进行重排。
        /// </summary>
        private async Task<List<ScoredChunk>> TryRerankAsync(List<ScoredChunk> candidates, string queryText, string modelOverride, CancellationToken cancellationToken)
        {
            if (candidates == null || candidates.Count <= 1 || _modelService == null)
            {
                return candidates;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                string prompt = BuildRerankPrompt(queryText, candidates);
                string response = await _modelService.ChatWithPromptsAsync(
                    "You are a retrieval reranker. Return only ordered chunk ids separated by commas.",
                    prompt,
                    modelOverride,
                    0.0d,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(response))
                {
                    return candidates;
                }

                string[] orderedIds = response.Split(new[] { ',', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var rankMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < orderedIds.Length; i++)
                {
                    string id = orderedIds[i].Trim();
                    if (!rankMap.ContainsKey(id))
                    {
                        rankMap[id] = i;
                    }
                }

                for (int i = 0; i < candidates.Count; i++)
                {
                    int rank;
                    if (rankMap.TryGetValue(candidates[i].Chunk.ChunkId, out rank))
                    {
                        // 以轻量加分方式融入重排结果，避免完全推翻基础分数。
                        candidates[i].TotalScore += (candidates.Count - rank) * 0.01d;
                    }
                }
            }
            catch
            {
                // 重排失败不影响主流程，保持原始排序。
            }

            return candidates;
        }

        /// <summary>
        /// 构建重排提示词。
        /// </summary>
        private static string BuildRerankPrompt(string queryText, List<ScoredChunk> candidates)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Question:");
            builder.AppendLine(queryText ?? string.Empty);
            builder.AppendLine();
            builder.AppendLine("Candidates:");
            for (int i = 0; i < candidates.Count; i++)
            {
                builder.Append(candidates[i].Chunk.ChunkId)
                    .Append(": ")
                    .AppendLine(candidates[i].Chunk.Text);
            }

            builder.AppendLine();
            builder.Append("Output chunk ids in relevance order, e.g. p3,p1,p2");
            return builder.ToString();
        }

        /// <summary>
        /// 基于关键词重叠计算候选分片分数。
        /// </summary>
        private static List<ScoredChunk> ScoreByKeyword(List<DocumentChunk> chunks, string queryText)
        {
            var queryTokens = Tokenize(queryText);
            if (queryTokens.Count == 0)
            {
                queryTokens.Add("文档");
            }

            var result = new List<ScoredChunk>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                List<string> chunkTokens = Tokenize(chunks[i].Text);
                int overlap = CountOverlap(queryTokens, chunkTokens);
                double score = queryTokens.Count == 0 ? 0d : (double)overlap / queryTokens.Count;

                result.Add(new ScoredChunk
                {
                    Chunk = chunks[i],
                    KeywordScore = score,
                    VectorScore = 0d,
                    TotalScore = score
                });
            }

            return result;
        }

        /// <summary>
        /// 统计两个 token 集合的重叠数量。
        /// </summary>
        private static int CountOverlap(List<string> left, List<string> right)
        {
            var rightSet = new HashSet<string>(right, StringComparer.OrdinalIgnoreCase);
            int count = 0;
            for (int i = 0; i < left.Count; i++)
            {
                if (rightSet.Contains(left[i]))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 对文本进行粗粒度分词。
        /// </summary>
        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return tokens;
            }

            MatchCollection matches = Regex.Matches(text.ToLowerInvariant(), "[a-z0-9\u4e00-\u9fa5]+");
            for (int i = 0; i < matches.Count; i++)
            {
                string token = matches[i].Value.Trim();
                if (token.Length >= 2)
                {
                    tokens.Add(token);
                }
            }

            return tokens;
        }

        /// <summary>
        /// 构建检索查询文本（用户问题 + 选区上下文）。
        /// </summary>
        private static string BuildQueryText(DocumentQuery query)
        {
            if (query == null)
            {
                return string.Empty;
            }

            return (query.QueryText ?? string.Empty) + "\n" + (query.SelectedText ?? string.Empty);
        }

        /// <summary>
        /// 计算余弦相似度。
        /// </summary>
        private static double CosineSimilarity(float[] left, float[] right)
        {
            if (left == null || right == null || left.Length == 0 || right.Length == 0)
            {
                return 0d;
            }

            int len = Math.Min(left.Length, right.Length);
            double dot = 0d;
            double normLeft = 0d;
            double normRight = 0d;

            for (int i = 0; i < len; i++)
            {
                dot += left[i] * right[i];
                normLeft += left[i] * left[i];
                normRight += right[i] * right[i];
            }

            if (normLeft <= 0d || normRight <= 0d)
            {
                return 0d;
            }

            return dot / (Math.Sqrt(normLeft) * Math.Sqrt(normRight));
        }

        /// <summary>
        /// 带分值的分片结构。
        /// </summary>
        private sealed class ScoredChunk
        {
            public DocumentChunk Chunk { get; set; }

            public double KeywordScore { get; set; }

            public double VectorScore { get; set; }

            public double TotalScore { get; set; }
        }
    }
}
