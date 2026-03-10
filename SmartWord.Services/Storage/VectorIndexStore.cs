using SmartWord.Core.Abstractions.Conversation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// 文件说明：
// 向量索引文件存储，实现分片向量缓存、增量更新与过期清理。
namespace SmartWord.Services.Storage
{
    /// <summary>
    /// 向量索引存储。
    /// </summary>
    public sealed class VectorIndexStore
    {
        private readonly string _baseDirectory;
        private readonly object _syncRoot = new object();

        /// <summary>
        /// 初始化向量索引存储。
        /// </summary>
        /// <param name="baseDirectory">索引目录。</param>
        public VectorIndexStore(string baseDirectory)
        {
            _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "vector-index")
                : baseDirectory;
        }

        /// <summary>
        /// 获取或创建文档分片向量缓存。
        /// </summary>
        /// <param name="documentId">文档 ID。</param>
        /// <param name="chunks">分片列表。</param>
        /// <param name="embeddingService">向量服务。</param>
        /// <param name="modelOverride">模型覆盖项。</param>
        /// <returns>分片 ID 到向量的映射。</returns>
        public Task<Dictionary<string, float[]>> GetOrCreateEmbeddingsAsync(
            string documentId,
            IList<DocumentChunk> chunks,
            IEmbeddingService embeddingService,
            string modelOverride)
        {
            if (chunks == null)
            {
                return Task.FromResult(new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase));
            }

            lock (_syncRoot)
            {
                EnsureDirectory();
                string filePath = ResolveFilePath(documentId);
                VectorIndexFile fileModel = LoadIndexFile(filePath);
                if (fileModel.Chunks == null)
                {
                    fileModel.Chunks = new List<VectorChunk>();
                }

                var output = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < chunks.Count; i++)
                {
                    DocumentChunk chunk = chunks[i];
                    if (chunk == null || string.IsNullOrWhiteSpace(chunk.ChunkId))
                    {
                        continue;
                    }

                    string textHash = ComputeSha1(chunk.Text ?? string.Empty);
                    VectorChunk item = FindChunk(fileModel.Chunks, chunk.ChunkId);
                    if (item == null || !string.Equals(item.TextHash, textHash, StringComparison.OrdinalIgnoreCase) || item.Vector == null || item.Vector.Length == 0)
                    {
                        // 文本变更或缓存缺失时才重新计算向量，降低重复调用成本。
                        float[] vector = embeddingService == null
                            ? new float[0]
                            : embeddingService.CreateEmbeddingAsync(chunk.Text ?? string.Empty, modelOverride, CancellationToken.None).GetAwaiter().GetResult();

                        if (item == null)
                        {
                            item = new VectorChunk();
                            item.ChunkId = chunk.ChunkId;
                            fileModel.Chunks.Add(item);
                        }

                        item.TextHash = textHash;
                        item.Vector = vector ?? new float[0];
                        item.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
                    }

                    output[chunk.ChunkId] = item.Vector ?? new float[0];
                }

                // 清理已不存在的分块，避免索引无限增长。
                CleanupStaleChunks(fileModel.Chunks, chunks);
                SaveIndexFile(filePath, fileModel);

                return Task.FromResult(output);
            }
        }

        /// <summary>
        /// 清理已不存在于最新快照中的缓存分片。
        /// </summary>
        private static void CleanupStaleChunks(List<VectorChunk> existing, IList<DocumentChunk> latest)
        {
            if (existing == null || latest == null)
            {
                return;
            }

            var latestSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < latest.Count; i++)
            {
                if (latest[i] != null && !string.IsNullOrWhiteSpace(latest[i].ChunkId))
                {
                    latestSet.Add(latest[i].ChunkId);
                }
            }

            for (int i = existing.Count - 1; i >= 0; i--)
            {
                if (!latestSet.Contains(existing[i].ChunkId))
                {
                    existing.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 按分片 ID 查找向量缓存项。
        /// </summary>
        private static VectorChunk FindChunk(List<VectorChunk> chunks, string chunkId)
        {
            if (chunks == null)
            {
                return null;
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                if (string.Equals(chunks[i].ChunkId, chunkId, StringComparison.OrdinalIgnoreCase))
                {
                    return chunks[i];
                }
            }

            return null;
        }

        /// <summary>
        /// 加载索引文件。
        /// </summary>
        private VectorIndexFile LoadIndexFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return new VectorIndexFile();
            }

            string json = File.ReadAllText(filePath, Encoding.UTF8);
            VectorIndexFile model = Deserialize<VectorIndexFile>(json);
            return model ?? new VectorIndexFile();
        }

        /// <summary>
        /// 保存索引文件。
        /// </summary>
        private void SaveIndexFile(string filePath, VectorIndexFile fileModel)
        {
            string json = Serialize(fileModel);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 解析文档对应的索引文件路径。
        /// </summary>
        private string ResolveFilePath(string documentId)
        {
            string id = string.IsNullOrWhiteSpace(documentId) ? "active-document" : documentId;
            string safeId = ComputeSha1(id);
            return Path.Combine(_baseDirectory, safeId + ".json");
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
        private sealed class VectorIndexFile
        {
            /// <summary>
            /// 初始化索引文件模型。
            /// </summary>
            public VectorIndexFile()
            {
                Chunks = new List<VectorChunk>();
            }

            /// <summary>
            /// 向量分片集合。
            /// </summary>
            [DataMember(Name = "chunks")]
            public List<VectorChunk> Chunks { get; set; }
        }

        [DataContract]
        private sealed class VectorChunk
        {
            /// <summary>
            /// 分片 ID。
            /// </summary>
            [DataMember(Name = "chunkId")]
            public string ChunkId { get; set; }

            /// <summary>
            /// 分片文本哈希。
            /// </summary>
            [DataMember(Name = "textHash")]
            public string TextHash { get; set; }

            /// <summary>
            /// 向量值。
            /// </summary>
            [DataMember(Name = "vector")]
            public float[] Vector { get; set; }

            /// <summary>
            /// 最近更新时间（UTC ticks）。
            /// </summary>
            [DataMember(Name = "updatedAtUtcTicks")]
            public long UpdatedAtUtcTicks { get; set; }
        }
    }

    /// <summary>
    /// 文档分片模型。
    /// </summary>
    public sealed class DocumentChunk
    {
        /// <summary>
        /// 分片 ID。
        /// </summary>
        public string ChunkId { get; set; }

        /// <summary>
        /// 分片文本哈希。
        /// </summary>
        public string TextHash { get; set; }

        /// <summary>
        /// 分片文本。
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// 分片在文档中的位置。
        /// </summary>
        public int Position { get; set; }

        /// <summary>
        /// 分片在文档中的结束位置。
        /// </summary>
        public int EndPosition { get; set; }

        /// <summary>
        /// 分片词项数量。
        /// </summary>
        public int TokenCount { get; set; }

        /// <summary>
        /// 分片类型（如 Paragraph/Heading/TableCell）。
        /// </summary>
        public string ChunkType { get; set; }

        /// <summary>
        /// 分片所属章节路径。
        /// </summary>
        public string HeadingPath { get; set; }

        /// <summary>
        /// 分片首段样式名。
        /// </summary>
        public string StyleName { get; set; }

        /// <summary>
        /// 结构权威度分值（标题、定义、表格等会更高）。
        /// </summary>
        public double AuthorityScore { get; set; }
    }
}
