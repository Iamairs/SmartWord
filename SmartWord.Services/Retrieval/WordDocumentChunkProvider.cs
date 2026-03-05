using SmartWord.Services.Storage;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

// 文件说明：
// Word 文档分块提供器，负责抓取活动文档快照并按段落生成检索分片。
namespace SmartWord.Services.Retrieval
{
    /// <summary>
    /// Word 文档分块提供器。
    /// </summary>
    public sealed class WordDocumentChunkProvider
    {
        private readonly dynamic _wordApplication;

        /// <summary>
        /// 初始化文档分块提供器。
        /// </summary>
        /// <param name="wordApplication">Word 应用实例。</param>
        public WordDocumentChunkProvider(dynamic wordApplication)
        {
            _wordApplication = wordApplication;
        }

        /// <summary>
        /// 抓取当前文档快照并生成分片。
        /// </summary>
        /// <returns>文档快照。</returns>
        public DocumentSnapshot CaptureSnapshot()
        {
            var snapshot = new DocumentSnapshot();
            if (_wordApplication == null)
            {
                return snapshot;
            }

            dynamic document = _wordApplication.ActiveDocument;
            if (document == null)
            {
                return snapshot;
            }

            string fullName = SafeToString(document.FullName);
            string name = SafeToString(document.Name);
            string content = SafeToString(document.Content == null ? null : document.Content.Text);

            // 通过文档元数据与长度构建稳定 ID，用于索引缓存复用。
            snapshot.DocumentId = BuildDocumentId(fullName, name, content);
            snapshot.Chunks = ExtractParagraphChunks(document);
            return snapshot;
        }

        /// <summary>
        /// 以段落为单位提取检索分片。
        /// </summary>
        private static List<DocumentChunk> ExtractParagraphChunks(dynamic document)
        {
            var chunks = new List<DocumentChunk>();
            if (document == null)
            {
                return chunks;
            }

            dynamic paragraphs = document.Paragraphs;
            if (paragraphs == null)
            {
                return chunks;
            }

            int count = 0;
            try
            {
                count = paragraphs.Count;
            }
            catch
            {
                // COM 读取失败时按空文档处理。
                count = 0;
            }

            for (int i = 1; i <= count; i++)
            {
                string text = string.Empty;
                try
                {
                    text = SafeToString(paragraphs[i].Range.Text);
                }
                catch
                {
                    text = string.Empty;
                }

                text = Normalize(text);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                chunks.Add(new DocumentChunk
                {
                    ChunkId = "p" + i,
                    Position = i,
                    Text = text
                });
            }

            return chunks;
        }

        /// <summary>
        /// 标准化段落文本，移除换行与多余空白。
        /// </summary>
        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized = text.Replace("\r", " ").Replace("\n", " ").Replace("\a", " ").Trim();
            while (normalized.Contains("  "))
            {
                normalized = normalized.Replace("  ", " ");
            }

            return normalized;
        }

        /// <summary>
        /// 构建文档 ID。
        /// </summary>
        private static string BuildDocumentId(string fullName, string name, string content)
        {
            string raw = (fullName ?? string.Empty) + "|" + (name ?? string.Empty) + "|" + (content ?? string.Empty).Length;
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(raw);
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
        /// 安全转字符串。
        /// </summary>
        private static string SafeToString(object value)
        {
            return value as string ?? string.Empty;
        }
    }

    /// <summary>
    /// 文档快照。
    /// </summary>
    public sealed class DocumentSnapshot
    {
        /// <summary>
        /// 初始化快照对象。
        /// </summary>
        public DocumentSnapshot()
        {
            Chunks = new List<DocumentChunk>();
            DocumentId = string.Empty;
        }

        /// <summary>
        /// 文档标识。
        /// </summary>
        public string DocumentId { get; set; }

        /// <summary>
        /// 文档分片集合。
        /// </summary>
        public List<DocumentChunk> Chunks { get; set; }
    }
}
