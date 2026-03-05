using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Core.Models.Conversation;
using SmartWord.Services.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SmartWord.Services.Retrieval
{
    public sealed class HybridDocumentRetriever : IDocumentRetriever
    {
        private readonly WordDocumentChunkProvider _chunkProvider;
        private readonly IEmbeddingService _embeddingService;
        private readonly VectorIndexStore _vectorIndexStore;
        private readonly IModelService _modelService;

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

        public async Task<RetrievedContext> RetrieveAsync(DocumentQuery query)
        {
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
            List<ScoredChunk> candidates = ScoreByKeyword(snapshot.Chunks, queryText);

            Dictionary<string, float[]> vectorMap = await _vectorIndexStore.GetOrCreateEmbeddingsAsync(
                snapshot.DocumentId,
                snapshot.Chunks,
                _embeddingService,
                query == null ? null : query.ModelOverride);

            float[] queryVector = _embeddingService == null
                ? new float[0]
                : await _embeddingService.CreateEmbeddingAsync(queryText, query == null ? null : query.ModelOverride);

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

            List<ScoredChunk> reranked = await TryRerankAsync(topCandidates, queryText, query == null ? null : query.ModelOverride);
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

        private async Task<List<ScoredChunk>> TryRerankAsync(List<ScoredChunk> candidates, string queryText, string modelOverride)
        {
            if (candidates == null || candidates.Count <= 1 || _modelService == null)
            {
                return candidates;
            }

            try
            {
                string prompt = BuildRerankPrompt(queryText, candidates);
                string response = await _modelService.ChatWithPromptsAsync(
                    "You are a retrieval reranker. Return only ordered chunk ids separated by commas.",
                    prompt,
                    modelOverride,
                    0.0d);

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

        private static string BuildQueryText(DocumentQuery query)
        {
            if (query == null)
            {
                return string.Empty;
            }

            return (query.QueryText ?? string.Empty) + "\n" + (query.SelectedText ?? string.Empty);
        }

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

        private sealed class ScoredChunk
        {
            public DocumentChunk Chunk { get; set; }

            public double KeywordScore { get; set; }

            public double VectorScore { get; set; }

            public double TotalScore { get; set; }
        }
    }
}
