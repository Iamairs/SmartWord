using SmartWord.Services.Storage;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SmartWord.Services.Retrieval
{
    public sealed class WordDocumentChunkProvider
    {
        private readonly dynamic _wordApplication;

        public WordDocumentChunkProvider(dynamic wordApplication)
        {
            _wordApplication = wordApplication;
        }

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

            snapshot.DocumentId = BuildDocumentId(fullName, name, content);
            snapshot.Chunks = ExtractParagraphChunks(document);
            return snapshot;
        }

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

        private static string SafeToString(object value)
        {
            return value as string ?? string.Empty;
        }
    }

    public sealed class DocumentSnapshot
    {
        public DocumentSnapshot()
        {
            Chunks = new List<DocumentChunk>();
            DocumentId = string.Empty;
        }

        public string DocumentId { get; set; }

        public List<DocumentChunk> Chunks { get; set; }
    }
}
