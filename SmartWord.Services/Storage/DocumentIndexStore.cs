using SmartWord.Core.Abstractions.Conversation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// 文件说明：
// 文档索引文件存储，负责分片元数据、向量缓存与词频统计的增量维护。
namespace SmartWord.Services.Storage
{
    /// <summary>
    /// 文档索引存储。
    /// </summary>
    public sealed class DocumentIndexStore
    {
        private const int CurrentSchemaVersion = 3;

        private readonly string _baseDirectory;
        private readonly object _gateSyncRoot = new object();
        private readonly Dictionary<string, SemaphoreSlim> _documentGates;

        /// <summary>
        /// 初始化文档索引存储。
        /// </summary>
        /// <param name="baseDirectory">索引目录。</param>
        public DocumentIndexStore(string baseDirectory)
        {
            _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "vector-index")
                : baseDirectory;
            _documentGates = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取或创建文档索引快照。
        /// </summary>
        /// <param name="documentId">文档 ID。</param>
        /// <param name="chunks">文档分片。</param>
        /// <param name="embeddingService">向量服务。</param>
        /// <param name="modelOverride">模型覆盖项。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>索引快照。</returns>
        public async Task<DocumentIndexSnapshot> GetOrCreateIndexAsync(
            string documentId,
            IList<DocumentChunk> chunks,
            IEmbeddingService embeddingService,
            string modelOverride,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var safeChunks = chunks ?? new List<DocumentChunk>();
            string filePath = ResolveFilePath(documentId);
            SemaphoreSlim gate = GetDocumentGate(filePath);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureDirectory();

                DocumentIndexFile fileModel = LoadIndexFile(filePath);
                if (fileModel == null)
                {
                    fileModel = new DocumentIndexFile();
                }

                List<IndexedChunkRecord> latestChunkRecords = BuildChunkRecords(safeChunks);
                var activeHashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < latestChunkRecords.Count; i++)
                {
                    activeHashSet.Add(latestChunkRecords[i].TextHash);
                }

                var embeddingMap = BuildEmbeddingMap(fileModel.Embeddings);
                var missingHashes = new List<string>();
                var missingTexts = new List<string>();
                for (int i = 0; i < latestChunkRecords.Count; i++)
                {
                    string textHash = latestChunkRecords[i].TextHash;
                    float[] vector;
                    if (!embeddingMap.TryGetValue(textHash, out vector) || vector == null || vector.Length == 0)
                    {
                        if (!missingHashes.Contains(textHash, StringComparer.OrdinalIgnoreCase))
                        {
                            missingHashes.Add(textHash);
                            missingTexts.Add(latestChunkRecords[i].Text ?? string.Empty);
                        }
                    }
                }

                if (missingHashes.Count > 0)
                {
                    IReadOnlyList<float[]> vectors = await CreateEmbeddingsAsync(
                        embeddingService,
                        missingTexts,
                        modelOverride,
                        cancellationToken).ConfigureAwait(false);

                    for (int i = 0; i < missingHashes.Count; i++)
                    {
                        float[] vector = i < vectors.Count && vectors[i] != null ? vectors[i] : new float[0];
                        embeddingMap[missingHashes[i]] = vector;
                    }
                }

                CleanupStaleEmbeddings(embeddingMap, activeHashSet);
                fileModel.SchemaVersion = CurrentSchemaVersion;
                fileModel.Chunks = latestChunkRecords;
                fileModel.Embeddings = BuildEmbeddingRecords(embeddingMap);
                SaveIndexFileAtomic(filePath, fileModel);

                return BuildSnapshot(latestChunkRecords, embeddingMap);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 创建分片向量。
        /// </summary>
        private static async Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(
            IEmbeddingService embeddingService,
            IReadOnlyList<string> texts,
            string modelOverride,
            CancellationToken cancellationToken)
        {
            if (texts == null || texts.Count == 0)
            {
                return new List<float[]>();
            }

            if (embeddingService == null)
            {
                var emptyVectors = new List<float[]>(texts.Count);
                for (int i = 0; i < texts.Count; i++)
                {
                    emptyVectors.Add(new float[0]);
                }

                return emptyVectors;
            }

            IBatchEmbeddingService batchEmbeddingService = embeddingService as IBatchEmbeddingService;
            if (batchEmbeddingService != null)
            {
                return await batchEmbeddingService
                    .CreateEmbeddingsAsync(texts, modelOverride, cancellationToken)
                    .ConfigureAwait(false);
            }

            var output = new List<float[]>(texts.Count);
            for (int i = 0; i < texts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float[] vector = await embeddingService
                    .CreateEmbeddingAsync(texts[i], modelOverride, cancellationToken)
                    .ConfigureAwait(false);
                output.Add(vector ?? new float[0]);
            }

            return output;
        }

        /// <summary>
        /// 构建分片记录。
        /// </summary>
        private static List<IndexedChunkRecord> BuildChunkRecords(IList<DocumentChunk> chunks)
        {
            var output = new List<IndexedChunkRecord>();
            if (chunks == null || chunks.Count == 0)
            {
                return output;
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                DocumentChunk chunk = chunks[i];
                if (chunk == null || string.IsNullOrWhiteSpace(chunk.Text))
                {
                    continue;
                }

                string text = chunk.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                string textHash = string.IsNullOrWhiteSpace(chunk.TextHash)
                    ? ComputeSha1(text)
                    : chunk.TextHash.Trim().ToLowerInvariant();
                Dictionary<string, int> termFrequencies = BuildTermFrequencies(text, chunk);
                int tokenCount = termFrequencies.Values.Sum();

                chunk.TextHash = textHash;
                chunk.TokenCount = tokenCount;

                output.Add(new IndexedChunkRecord
                {
                    ChunkId = string.IsNullOrWhiteSpace(chunk.ChunkId) ? "chunk-" + (i + 1) : chunk.ChunkId,
                    TextHash = textHash,
                    Position = chunk.Position,
                    EndPosition = chunk.EndPosition <= 0 ? chunk.Position : chunk.EndPosition,
                    Text = text,
                    TokenCount = tokenCount,
                    TermFrequencies = termFrequencies,
                    ChunkType = string.IsNullOrWhiteSpace(chunk.ChunkType) ? "Paragraph" : chunk.ChunkType,
                    HeadingPath = chunk.HeadingPath ?? string.Empty,
                    StyleName = chunk.StyleName ?? string.Empty,
                    AuthorityScore = chunk.AuthorityScore
                });
            }

            return output;
        }

        /// <summary>
        /// 构建词频字典。
        /// </summary>
        private static Dictionary<string, int> BuildTermFrequencies(string text, DocumentChunk chunk)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            MatchCollection matches = Regex.Matches((text ?? string.Empty).ToLowerInvariant(), "[a-z0-9]+|[\u4e00-\u9fa5]+");
            for (int i = 0; i < matches.Count; i++)
            {
                string token = matches[i].Value.Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (IsChineseToken(token))
                {
                    AddChineseNgrams(map, token);
                }
                else
                {
                    if (token.Length < 2)
                    {
                        continue;
                    }

                    int count;
                    if (!map.TryGetValue(token, out count))
                    {
                        map[token] = 1;
                    }
                    else
                    {
                        map[token] = count + 1;
                    }
                }
            }

            AddStructureTerms(map, chunk);

            return map;
        }

        /// <summary>
        /// 注入结构字段词项，增强标题/表格类查询的词法召回稳定性。
        /// </summary>
        private static void AddStructureTerms(Dictionary<string, int> map, DocumentChunk chunk)
        {
            if (map == null || chunk == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(chunk.HeadingPath))
            {
                MatchCollection headingTokens = Regex.Matches(chunk.HeadingPath.ToLowerInvariant(), "[a-z0-9]+|[\u4e00-\u9fa5]+");
                for (int i = 0; i < headingTokens.Count; i++)
                {
                    string token = headingTokens[i].Value;
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    if (IsChineseToken(token))
                    {
                        AddChineseNgrams(map, token);
                    }
                    else if (token.Length >= 2)
                    {
                        Increase(map, token);
                        Increase(map, token);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(chunk.ChunkType))
            {
                string type = chunk.ChunkType.Trim().ToLowerInvariant();
                if (type == "heading")
                {
                    Increase(map, "标题");
                }
                else if (type == "tablecell")
                {
                    Increase(map, "表格");
                }
            }
        }

        /// <summary>
        /// 判断 token 是否由中文字符构成。
        /// </summary>
        private static bool IsChineseToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (c < '\u4e00' || c > '\u9fa5')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 为中文 token 生成 2-gram 词项。
        /// </summary>
        private static void AddChineseNgrams(Dictionary<string, int> map, string token)
        {
            if (map == null || string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            if (token.Length == 1)
            {
                Increase(map, token);
                return;
            }

            for (int i = 0; i < token.Length - 1; i++)
            {
                Increase(map, token.Substring(i, 2));
            }
        }

        /// <summary>
        /// 增加词项计数。
        /// </summary>
        private static void Increase(Dictionary<string, int> map, string token)
        {
            int count;
            if (!map.TryGetValue(token, out count))
            {
                map[token] = 1;
            }
            else
            {
                map[token] = count + 1;
            }
        }

        /// <summary>
        /// 清理过期向量缓存。
        /// </summary>
        private static void CleanupStaleEmbeddings(Dictionary<string, float[]> embeddingMap, HashSet<string> activeHashSet)
        {
            if (embeddingMap == null || activeHashSet == null)
            {
                return;
            }

            List<string> keys = embeddingMap.Keys.ToList();
            for (int i = 0; i < keys.Count; i++)
            {
                if (!activeHashSet.Contains(keys[i]))
                {
                    embeddingMap.Remove(keys[i]);
                }
            }
        }

        /// <summary>
        /// 构建内存索引快照。
        /// </summary>
        private static DocumentIndexSnapshot BuildSnapshot(List<IndexedChunkRecord> chunkRecords, Dictionary<string, float[]> embeddingMap)
        {
            var chunks = new List<IndexedChunk>(chunkRecords.Count);
            var documentFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            double tokenCountSum = 0d;

            for (int i = 0; i < chunkRecords.Count; i++)
            {
                IndexedChunkRecord record = chunkRecords[i];
                float[] vector;
                if (!embeddingMap.TryGetValue(record.TextHash, out vector) || vector == null)
                {
                    vector = new float[0];
                }

                tokenCountSum += Math.Max(1, record.TokenCount);
                if (record.TermFrequencies != null)
                {
                    foreach (KeyValuePair<string, int> pair in record.TermFrequencies)
                    {
                        int count;
                        if (!documentFrequency.TryGetValue(pair.Key, out count))
                        {
                            documentFrequency[pair.Key] = 1;
                        }
                        else
                        {
                            documentFrequency[pair.Key] = count + 1;
                        }
                    }
                }

                chunks.Add(new IndexedChunk
                {
                    ChunkId = record.ChunkId,
                    TextHash = record.TextHash,
                    Position = record.Position,
                    EndPosition = record.EndPosition,
                    Text = record.Text,
                    TokenCount = record.TokenCount,
                    TermFrequencies = record.TermFrequencies ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    Embedding = vector,
                    ChunkType = string.IsNullOrWhiteSpace(record.ChunkType) ? "Paragraph" : record.ChunkType,
                    HeadingPath = record.HeadingPath ?? string.Empty,
                    StyleName = record.StyleName ?? string.Empty,
                    AuthorityScore = record.AuthorityScore
                });
            }

            double averageTokenCount = chunks.Count == 0 ? 0d : tokenCountSum / chunks.Count;
            return new DocumentIndexSnapshot
            {
                Chunks = chunks,
                EmbeddingsByTextHash = embeddingMap,
                DocumentFrequency = documentFrequency,
                AverageTokenCount = averageTokenCount
            };
        }

        /// <summary>
        /// 构建向量字典。
        /// </summary>
        private static Dictionary<string, float[]> BuildEmbeddingMap(List<EmbeddingRecord> records)
        {
            var map = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            if (records == null)
            {
                return map;
            }

            for (int i = 0; i < records.Count; i++)
            {
                EmbeddingRecord record = records[i];
                if (record == null || string.IsNullOrWhiteSpace(record.TextHash))
                {
                    continue;
                }

                map[record.TextHash] = record.Vector ?? new float[0];
            }

            return map;
        }

        /// <summary>
        /// 构建向量记录列表。
        /// </summary>
        private static List<EmbeddingRecord> BuildEmbeddingRecords(Dictionary<string, float[]> map)
        {
            var output = new List<EmbeddingRecord>();
            if (map == null)
            {
                return output;
            }

            foreach (KeyValuePair<string, float[]> pair in map)
            {
                output.Add(new EmbeddingRecord
                {
                    TextHash = pair.Key,
                    Vector = pair.Value ?? new float[0],
                    UpdatedAtUtcTicks = DateTime.UtcNow.Ticks
                });
            }

            return output;
        }

        /// <summary>
        /// 获取文档锁。
        /// </summary>
        private SemaphoreSlim GetDocumentGate(string key)
        {
            lock (_gateSyncRoot)
            {
                SemaphoreSlim gate;
                if (!_documentGates.TryGetValue(key, out gate))
                {
                    gate = new SemaphoreSlim(1, 1);
                    _documentGates[key] = gate;
                }

                return gate;
            }
        }

        /// <summary>
        /// 加载索引文件。
        /// </summary>
        private static DocumentIndexFile LoadIndexFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return new DocumentIndexFile();
            }

            string json = File.ReadAllText(filePath, Encoding.UTF8);
            DocumentIndexFile model = Deserialize<DocumentIndexFile>(json);
            return model ?? new DocumentIndexFile();
        }

        /// <summary>
        /// 原子写入索引文件。
        /// </summary>
        private static void SaveIndexFileAtomic(string filePath, DocumentIndexFile fileModel)
        {
            string json = Serialize(fileModel);
            string tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json, Encoding.UTF8);

            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }
        }

        /// <summary>
        /// 解析文档对应的索引文件路径。
        /// </summary>
        private string ResolveFilePath(string documentId)
        {
            string id = string.IsNullOrWhiteSpace(documentId) ? "active-document" : documentId.Trim();
            string safeId = ComputeSha1(id);
            return Path.Combine(_baseDirectory, safeId + ".index.json");
        }

        /// <summary>
        /// 确保索引目录存在。
        /// </summary>
        private void EnsureDirectory()
        {
            if (!Directory.Exists(_baseDirectory))
            {
                Directory.CreateDirectory(_baseDirectory);
            }
        }

        /// <summary>
        /// 计算 SHA1 字符串摘要。
        /// </summary>
        private static string ComputeSha1(string value)
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                byte[] hash = sha1.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        /// <summary>
        /// 序列化对象为 JSON。
        /// </summary>
        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>
        /// 反序列化 JSON 为目标对象。
        /// </summary>
        private static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(bytes))
            {
                return serializer.ReadObject(stream) as T;
            }
        }

        [DataContract]
        private sealed class DocumentIndexFile
        {
            /// <summary>
            /// 初始化索引文件模型。
            /// </summary>
            public DocumentIndexFile()
            {
                SchemaVersion = CurrentSchemaVersion;
                Chunks = new List<IndexedChunkRecord>();
                Embeddings = new List<EmbeddingRecord>();
            }

            [DataMember(Name = "schemaVersion")]
            public int SchemaVersion { get; set; }

            [DataMember(Name = "chunks")]
            public List<IndexedChunkRecord> Chunks { get; set; }

            [DataMember(Name = "embeddings")]
            public List<EmbeddingRecord> Embeddings { get; set; }
        }

        [DataContract]
        private sealed class EmbeddingRecord
        {
            [DataMember(Name = "textHash")]
            public string TextHash { get; set; }

            [DataMember(Name = "vector")]
            public float[] Vector { get; set; }

            [DataMember(Name = "updatedAtUtcTicks")]
            public long UpdatedAtUtcTicks { get; set; }
        }

        [DataContract]
        private sealed class IndexedChunkRecord
        {
            [DataMember(Name = "chunkId")]
            public string ChunkId { get; set; }

            [DataMember(Name = "textHash")]
            public string TextHash { get; set; }

            [DataMember(Name = "position")]
            public int Position { get; set; }

            [DataMember(Name = "endPosition")]
            public int EndPosition { get; set; }

            [DataMember(Name = "text")]
            public string Text { get; set; }

            [DataMember(Name = "tokenCount")]
            public int TokenCount { get; set; }

            [DataMember(Name = "termFrequencies")]
            public Dictionary<string, int> TermFrequencies { get; set; }

            [DataMember(Name = "chunkType")]
            public string ChunkType { get; set; }

            [DataMember(Name = "headingPath")]
            public string HeadingPath { get; set; }

            [DataMember(Name = "styleName")]
            public string StyleName { get; set; }

            [DataMember(Name = "authorityScore")]
            public double AuthorityScore { get; set; }
        }
    }

    /// <summary>
    /// 文档索引快照。
    /// </summary>
    public sealed class DocumentIndexSnapshot
    {
        /// <summary>
        /// 初始化索引快照。
        /// </summary>
        public DocumentIndexSnapshot()
        {
            Chunks = new List<IndexedChunk>();
            EmbeddingsByTextHash = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            DocumentFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 索引分片集合。
        /// </summary>
        public List<IndexedChunk> Chunks { get; set; }

        /// <summary>
        /// 文本哈希到向量的映射。
        /// </summary>
        public Dictionary<string, float[]> EmbeddingsByTextHash { get; set; }

        /// <summary>
        /// 词项文档频次。
        /// </summary>
        public Dictionary<string, int> DocumentFrequency { get; set; }

        /// <summary>
        /// 平均词项数量。
        /// </summary>
        public double AverageTokenCount { get; set; }
    }

    /// <summary>
    /// 索引分片。
    /// </summary>
    public sealed class IndexedChunk
    {
        /// <summary>
        /// 分片 ID。
        /// </summary>
        public string ChunkId { get; set; }

        /// <summary>
        /// 文本哈希。
        /// </summary>
        public string TextHash { get; set; }

        /// <summary>
        /// 起始位置。
        /// </summary>
        public int Position { get; set; }

        /// <summary>
        /// 结束位置。
        /// </summary>
        public int EndPosition { get; set; }

        /// <summary>
        /// 分片文本。
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// 词项数量。
        /// </summary>
        public int TokenCount { get; set; }

        /// <summary>
        /// 词频字典。
        /// </summary>
        public Dictionary<string, int> TermFrequencies { get; set; }

        /// <summary>
        /// 对应向量。
        /// </summary>
        public float[] Embedding { get; set; }

        /// <summary>
        /// 分片类型（Paragraph/Heading/TableCell）。
        /// </summary>
        public string ChunkType { get; set; }

        /// <summary>
        /// 章节路径。
        /// </summary>
        public string HeadingPath { get; set; }

        /// <summary>
        /// Word 样式名。
        /// </summary>
        public string StyleName { get; set; }

        /// <summary>
        /// 结构权威度分值。
        /// </summary>
        public double AuthorityScore { get; set; }
    }
}
