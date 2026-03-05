using SmartWord.Core.Abstractions.Conversation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SmartWord.Services.Storage
{
    public sealed class VectorIndexStore
    {
        private readonly string _baseDirectory;
        private readonly object _syncRoot = new object();

        public VectorIndexStore(string baseDirectory)
        {
            _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "vector-index")
                : baseDirectory;
        }

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
                        float[] vector = embeddingService == null
                            ? new float[0]
                            : embeddingService.CreateEmbeddingAsync(chunk.Text ?? string.Empty, modelOverride).GetAwaiter().GetResult();

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

        private void SaveIndexFile(string filePath, VectorIndexFile fileModel)
        {
            string json = Serialize(fileModel);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        private string ResolveFilePath(string documentId)
        {
            string id = string.IsNullOrWhiteSpace(documentId) ? "active-document" : documentId;
            string safeId = ComputeSha1(id);
            return Path.Combine(_baseDirectory, safeId + ".json");
        }

        private void EnsureDirectory()
        {
            if (!Directory.Exists(_baseDirectory))
            {
                Directory.CreateDirectory(_baseDirectory);
            }
        }

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

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

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
            public VectorIndexFile()
            {
                Chunks = new List<VectorChunk>();
            }

            [DataMember(Name = "chunks")]
            public List<VectorChunk> Chunks { get; set; }
        }

        [DataContract]
        private sealed class VectorChunk
        {
            [DataMember(Name = "chunkId")]
            public string ChunkId { get; set; }

            [DataMember(Name = "textHash")]
            public string TextHash { get; set; }

            [DataMember(Name = "vector")]
            public float[] Vector { get; set; }

            [DataMember(Name = "updatedAtUtcTicks")]
            public long UpdatedAtUtcTicks { get; set; }
        }
    }

    public sealed class DocumentChunk
    {
        public string ChunkId { get; set; }

        public string Text { get; set; }

        public int Position { get; set; }
    }
}
