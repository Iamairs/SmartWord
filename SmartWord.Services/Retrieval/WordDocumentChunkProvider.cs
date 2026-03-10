using SmartWord.Core.Abstractions;
using SmartWord.Services.Storage;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// 文件说明：
// Word 文档分块提供器，负责抓取活动文档快照并生成稳定分块。
namespace SmartWord.Services.Retrieval
{
    /// <summary>
    /// Word 文档分块提供器。
    /// </summary>
    public sealed class WordDocumentChunkProvider
    {
        private const string DocumentIdPropertyName = "SmartWordDocumentId";
        private const int MsoPropertyTypeString = 4;
        private const int TargetChunkChars = 560;
        private const int MinChunkChars = 180;
        private const int OverlapParagraphCount = 1;

        private readonly dynamic _wordApplication;
        private readonly IWordThreadInvoker _wordThreadInvoker;

        /// <summary>
        /// 初始化文档分块提供器。
        /// </summary>
        /// <param name="wordApplication">Word 应用实例。</param>
        /// <param name="wordThreadInvoker">Word 主线程调用器。</param>
        public WordDocumentChunkProvider(dynamic wordApplication, IWordThreadInvoker wordThreadInvoker)
        {
            _wordApplication = wordApplication;
            _wordThreadInvoker = wordThreadInvoker;
        }

        /// <summary>
        /// 抓取当前文档快照并生成分片。
        /// </summary>
        /// <returns>文档快照。</returns>
        public DocumentSnapshot CaptureSnapshot()
        {
            return InvokeOnWordThread(CaptureSnapshotCore);
        }

        /// <summary>
        /// 在 Word 主线程抓取当前文档快照。
        /// </summary>
        /// <returns>文档快照。</returns>
        private DocumentSnapshot CaptureSnapshotCore()
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
            snapshot.DocumentId = ResolveStableDocumentId(document, fullName, name);
            snapshot.Chunks = ExtractWindowChunks(document);
            return snapshot;
        }

        /// <summary>
        /// 解析稳定文档标识。
        /// </summary>
        private static string ResolveStableDocumentId(dynamic document, string fullName, string name)
        {
            string customId = ReadCustomDocumentId(document);
            if (string.IsNullOrWhiteSpace(customId))
            {
                customId = Guid.NewGuid().ToString("N");
                WriteCustomDocumentId(document, customId);
            }

            if (!string.IsNullOrWhiteSpace(customId))
            {
                return "doc-" + customId;
            }

            // 无法写入文档属性时降级到路径/名称哈希，保证可用性。
            string raw = (fullName ?? string.Empty) + "|" + (name ?? string.Empty);
            return "fallback-" + ComputeSha1(raw);
        }

        /// <summary>
        /// 读取文档自定义属性中的稳定 ID。
        /// </summary>
        private static string ReadCustomDocumentId(dynamic document)
        {
            if (document == null)
            {
                return string.Empty;
            }

            try
            {
                dynamic properties = document.CustomDocumentProperties;
                if (properties == null)
                {
                    return string.Empty;
                }

                dynamic property = properties[DocumentIdPropertyName];
                if (property == null)
                {
                    return string.Empty;
                }

                return SafeToString(property.Value).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 写入文档自定义属性中的稳定 ID。
        /// </summary>
        private static void WriteCustomDocumentId(dynamic document, string value)
        {
            if (document == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            try
            {
                dynamic properties = document.CustomDocumentProperties;
                if (properties == null)
                {
                    return;
                }

                try
                {
                    dynamic existing = properties[DocumentIdPropertyName];
                    if (existing != null)
                    {
                        existing.Value = value;
                        return;
                    }
                }
                catch
                {
                }

                properties.Add(DocumentIdPropertyName, false, MsoPropertyTypeString, value);
            }
            catch
            {
                // 企业策略禁用时静默降级到 fallback ID。
            }
        }

        /// <summary>
        /// 以窗口拼接方式提取检索分片，提升语义完整性与稳定性。
        /// </summary>
        private static List<DocumentChunk> ExtractWindowChunks(dynamic document)
        {
            var chunks = new List<DocumentChunk>();
            List<ParagraphUnit> paragraphs = ExtractParagraphUnits(document);
            if (paragraphs.Count == 0)
            {
                return chunks;
            }

            int cursor = 0;
            int ordinal = 1;
            while (cursor < paragraphs.Count)
            {
                int start = cursor;
                int end = start;
                int currentLength = 0;

                while (end < paragraphs.Count)
                {
                    int paragraphLength = paragraphs[end].Text.Length;
                    int nextLength = currentLength == 0 ? paragraphLength : currentLength + 1 + paragraphLength;
                    if (nextLength > TargetChunkChars && currentLength >= MinChunkChars)
                    {
                        break;
                    }

                    currentLength = nextLength;
                    end++;
                }

                if (end <= start)
                {
                    end = start + 1;
                }

                string mergedText = MergeParagraphRange(paragraphs, start, end);
                string normalized = Normalize(mergedText);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    string textHash = ComputeSha1(normalized);
                    int startPosition = paragraphs[start].Position;
                    int endPosition = paragraphs[end - 1].Position;
                    string chunkType = ResolveChunkType(paragraphs, start, end);
                    string headingPath = ResolveChunkHeadingPath(paragraphs, start, end);
                    string styleName = paragraphs[start].StyleName ?? string.Empty;
                    chunks.Add(new DocumentChunk
                    {
                        ChunkId = BuildChunkId(ordinal, textHash),
                        Position = startPosition,
                        EndPosition = endPosition,
                        Text = normalized,
                        TextHash = textHash,
                        TokenCount = EstimateTokenCount(normalized),
                        ChunkType = chunkType,
                        HeadingPath = headingPath,
                        StyleName = styleName,
                        AuthorityScore = ComputeAuthorityScore(chunkType, styleName, headingPath, normalized)
                    });
                    ordinal++;
                }

                if (end >= paragraphs.Count)
                {
                    break;
                }

                int nextCursor = end - OverlapParagraphCount;
                if (nextCursor <= start)
                {
                    nextCursor = start + 1;
                }

                cursor = nextCursor;
            }

            return chunks;
        }

        /// <summary>
        /// 解析窗口分片类型，优先识别表格，再识别标题。
        /// </summary>
        private static string ResolveChunkType(List<ParagraphUnit> paragraphs, int start, int endExclusive)
        {
            if (paragraphs == null || paragraphs.Count == 0 || start < 0 || endExclusive <= start)
            {
                return "Paragraph";
            }

            int total = 0;
            int tableCount = 0;
            bool hasHeading = false;
            for (int i = start; i < endExclusive && i < paragraphs.Count; i++)
            {
                total++;
                if (paragraphs[i].IsInTable)
                {
                    tableCount++;
                }

                if (paragraphs[i].IsHeading)
                {
                    hasHeading = true;
                }
            }

            if (total > 0 && tableCount * 2 >= total)
            {
                return "TableCell";
            }

            if (hasHeading)
            {
                return "Heading";
            }

            return "Paragraph";
        }

        /// <summary>
        /// 解析分片所属章节路径，优先取当前窗口，再回退到前序段落路径。
        /// </summary>
        private static string ResolveChunkHeadingPath(List<ParagraphUnit> paragraphs, int start, int endExclusive)
        {
            if (paragraphs == null || paragraphs.Count == 0)
            {
                return string.Empty;
            }

            for (int i = start; i < endExclusive && i < paragraphs.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(paragraphs[i].HeadingPath))
                {
                    return paragraphs[i].HeadingPath;
                }
            }

            for (int i = Math.Min(start - 1, paragraphs.Count - 1); i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(paragraphs[i].HeadingPath))
                {
                    return paragraphs[i].HeadingPath;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 抽取有效段落单元。
        /// </summary>
        private static List<ParagraphUnit> ExtractParagraphUnits(dynamic document)
        {
            var result = new List<ParagraphUnit>();
            if (document == null)
            {
                return result;
            }

            dynamic paragraphs = document.Paragraphs;
            if (paragraphs == null)
            {
                return result;
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

            string currentHeadingPath = string.Empty;
            for (int i = 1; i <= count; i++)
            {
                string text = string.Empty;
                dynamic paragraph = null;
                try
                {
                    paragraph = paragraphs[i];
                    text = SafeToString(paragraph.Range.Text);
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

                string styleName = ReadStyleName(paragraph);
                bool isHeading = IsHeadingStyle(styleName);
                bool isInTable = IsParagraphInTable(paragraph);
                string headingPath = string.Empty;
                if (isHeading)
                {
                    headingPath = TryExtractHeadingPath(text);
                    if (string.IsNullOrWhiteSpace(headingPath))
                    {
                        headingPath = text.Length > 28 ? text.Substring(0, 28) : text;
                    }

                    currentHeadingPath = headingPath;
                }
                else
                {
                    headingPath = currentHeadingPath;
                }

                result.Add(new ParagraphUnit
                {
                    Position = i,
                    Text = text,
                    StyleName = styleName,
                    IsHeading = isHeading,
                    IsInTable = isInTable,
                    HeadingPath = headingPath
                });
            }

            return result;
        }

        /// <summary>
        /// 读取段落样式名（尽量兼容 COM 动态对象与测试替身）。
        /// </summary>
        private static string ReadStyleName(dynamic paragraph)
        {
            if (paragraph == null)
            {
                return string.Empty;
            }

            try
            {
                dynamic range = paragraph.Range;
                if (range == null)
                {
                    return string.Empty;
                }

                dynamic styleObj = null;
                try
                {
                    styleObj = range.Style;
                }
                catch
                {
                    styleObj = null;
                }

                if (styleObj == null)
                {
                    return string.Empty;
                }

                string plain = styleObj as string;
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    return plain.Trim();
                }

                try
                {
                    string nameLocal = SafeToString(styleObj.NameLocal);
                    if (!string.IsNullOrWhiteSpace(nameLocal))
                    {
                        return nameLocal.Trim();
                    }
                }
                catch
                {
                }

                try
                {
                    string name = SafeToString(styleObj.Name);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name.Trim();
                    }
                }
                catch
                {
                }

                return SafeToString(styleObj).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 判断段落是否属于标题样式。
        /// </summary>
        private static bool IsHeadingStyle(string styleName)
        {
            if (string.IsNullOrWhiteSpace(styleName))
            {
                return false;
            }

            return Regex.IsMatch(styleName, "heading|标题|章标题|节标题", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 判断段落是否位于表格中。
        /// </summary>
        private static bool IsParagraphInTable(dynamic paragraph)
        {
            if (paragraph == null)
            {
                return false;
            }

            try
            {
                dynamic range = paragraph.Range;
                if (range == null)
                {
                    return false;
                }

                dynamic tables = range.Tables;
                if (tables == null)
                {
                    return false;
                }

                int count = 0;
                try
                {
                    count = Convert.ToInt32(tables.Count);
                }
                catch
                {
                    count = 0;
                }

                return count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从标题文本中抽取章节路径（编号或附录标记）。
        /// </summary>
        private static string TryExtractHeadingPath(string headingText)
        {
            string text = Normalize(headingText);
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            Match numberMatch = Regex.Match(text, @"^\s*(\d+(\.\d+){0,4})\b");
            if (numberMatch.Success)
            {
                return numberMatch.Groups[1].Value;
            }

            Match appendixMatch = Regex.Match(text, @"^\s*(附录\s*[A-Za-z0-9一二三四五六七八九十]+)");
            if (appendixMatch.Success)
            {
                return appendixMatch.Groups[1].Value;
            }

            Match chapterMatch = Regex.Match(text, @"^\s*(第[一二三四五六七八九十百千万0-9]+[章节条款编])");
            if (chapterMatch.Success)
            {
                return chapterMatch.Groups[1].Value;
            }

            return string.Empty;
        }

        /// <summary>
        /// 计算结构权威度，标题/表格/定义语句会得到更高分值。
        /// </summary>
        private static double ComputeAuthorityScore(string chunkType, string styleName, string headingPath, string text)
        {
            double score = 0.2d;
            if (string.Equals(chunkType, "Heading", StringComparison.OrdinalIgnoreCase))
            {
                score += 0.55d;
            }
            else if (string.Equals(chunkType, "TableCell", StringComparison.OrdinalIgnoreCase))
            {
                score += 0.42d;
            }

            if (IsHeadingStyle(styleName))
            {
                score += 0.2d;
            }

            if (!string.IsNullOrWhiteSpace(headingPath) &&
                Regex.IsMatch(headingPath, "附录|appendix", RegexOptions.IgnoreCase))
            {
                score += 0.08d;
            }

            if (Regex.IsMatch(text ?? string.Empty, "定义|是指|shall\\s+mean", RegexOptions.IgnoreCase))
            {
                score += 0.06d;
            }

            return Math.Max(0d, Math.Min(1d, score));
        }

        /// <summary>
        /// 合并段落窗口文本。
        /// </summary>
        private static string MergeParagraphRange(List<ParagraphUnit> paragraphs, int start, int endExclusive)
        {
            var builder = new StringBuilder();
            for (int i = start; i < endExclusive && i < paragraphs.Count; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(paragraphs[i].Text);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 估算文本 token 数。
        /// </summary>
        private static int EstimateTokenCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            int count = 0;
            bool inToken = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool isToken = char.IsLetterOrDigit(c) || (c >= '\u4e00' && c <= '\u9fa5');
                if (isToken && !inToken)
                {
                    count++;
                    inToken = true;
                }
                else if (!isToken)
                {
                    inToken = false;
                }
            }

            return count;
        }

        /// <summary>
        /// 标准化文本，移除控制符与多余空白。
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
        /// 构建分片标识。
        /// </summary>
        private static string BuildChunkId(int ordinal, string textHash)
        {
            string hashPrefix = string.IsNullOrWhiteSpace(textHash)
                ? "0000000000"
                : textHash.Substring(0, Math.Min(10, textHash.Length));
            return "c" + ordinal + "_" + hashPrefix;
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
        /// 安全转字符串。
        /// </summary>
        private static string SafeToString(object value)
        {
            return value as string ?? string.Empty;
        }

        /// <summary>
        /// 在 Word 主线程执行带返回值逻辑。
        /// </summary>
        private T InvokeOnWordThread<T>(Func<T> func)
        {
            if (func == null)
            {
                return default(T);
            }

            if (_wordThreadInvoker == null)
            {
                return func();
            }

            return _wordThreadInvoker.Invoke(func);
        }

        /// <summary>
        /// 段落单元。
        /// </summary>
        private sealed class ParagraphUnit
        {
            public int Position { get; set; }

            public string Text { get; set; }

            public string StyleName { get; set; }

            public bool IsHeading { get; set; }

            public bool IsInTable { get; set; }

            public string HeadingPath { get; set; }
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
