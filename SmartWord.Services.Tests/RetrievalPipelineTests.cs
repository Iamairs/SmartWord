using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Core.Models;
using SmartWord.Core.Models.Conversation;
using SmartWord.Services.Retrieval;
using SmartWord.Services.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SmartWord.Services.Tests;

[TestClass]
public sealed class RetrievalPipelineTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void CaptureSnapshot_ParagraphShift_DocumentIdKeepsStable()
    {
        var app = new FakeWordApplication
        {
            ActiveDocument = FakeDocument.Create(
                @"C:\docs\contract.docx",
                "contract.docx",
                new[]
                {
                    BuildLongParagraph("总则条款", 90),
                    BuildLongParagraph("付款条款", 90),
                    BuildLongParagraph("违约责任", 90)
                })
        };
        var provider = new WordDocumentChunkProvider(app, null);

        DocumentSnapshot first = provider.CaptureSnapshot();
        app.ActiveDocument.Paragraphs.Insert(1, BuildLongParagraph("封面补充", 90));
        DocumentSnapshot second = provider.CaptureSnapshot();

        Assert.IsFalse(string.IsNullOrWhiteSpace(first.DocumentId));
        Assert.AreEqual(first.DocumentId, second.DocumentId);
        Assert.IsTrue(second.Chunks.Count >= first.Chunks.Count);
    }

    [TestMethod]
    public async Task DocumentIndexStore_ChunkIdChangedWithSameText_ReusesEmbeddingCache()
    {
        string indexDir = CreateTempDir();
        try
        {
            var store = new DocumentIndexStore(indexDir);
            var embedding = new CountingEmbeddingService();

            var firstChunks = new List<DocumentChunk>
            {
                new DocumentChunk { ChunkId = "c1", Position = 1, EndPosition = 1, Text = "第一条 甲方应按时付款。", TextHash = "hash-a" },
                new DocumentChunk { ChunkId = "c2", Position = 2, EndPosition = 2, Text = "第二条 乙方应提供发票。", TextHash = "hash-b" }
            };

            await store.GetOrCreateIndexAsync("doc-1", firstChunks, embedding, string.Empty, CancellationToken.None);
            int firstInputCount = embedding.TotalInputCount;

            var secondChunks = new List<DocumentChunk>
            {
                new DocumentChunk { ChunkId = "c9", Position = 9, EndPosition = 9, Text = "第一条 甲方应按时付款。", TextHash = "hash-a" },
                new DocumentChunk { ChunkId = "c10", Position = 10, EndPosition = 10, Text = "第二条 乙方应提供发票。", TextHash = "hash-b" }
            };

            await store.GetOrCreateIndexAsync("doc-1", secondChunks, embedding, string.Empty, CancellationToken.None);

            Assert.AreEqual(2, firstInputCount);
            Assert.AreEqual(firstInputCount, embedding.TotalInputCount);
        }
        finally
        {
            DeleteDir(indexDir);
        }
    }

    [TestMethod]
    public async Task RetrieveAsync_Bm25DominantTerm_ReturnsRelevantChunkFirst()
    {
        string indexDir = CreateTempDir();
        try
        {
            var app = new FakeWordApplication
            {
                ActiveDocument = FakeDocument.Create(
                    @"C:\docs\qa.docx",
                    "qa.docx",
                    new[]
                    {
                        BuildLongParagraph("付款安排", 120),
                        BuildLongParagraph("违约责任", 140),
                        BuildLongParagraph("发票流程", 120)
                    })
            };

            var provider = new WordDocumentChunkProvider(app, null);
            var retriever = new HybridDocumentRetriever(
                provider,
                new ZeroEmbeddingService(),
                new DocumentIndexStore(indexDir),
                new NoopModelService());

            RetrievedContext context = await retriever.RetrieveAsync(new DocumentQuery
            {
                QueryText = "请解释违约责任",
                SelectedText = string.Empty,
                MaxChunks = 3,
                MaxContextCharacters = 2400
            });

            Assert.IsNotNull(context);
            Assert.IsTrue(context.Chunks.Count > 0);
            StringAssert.Contains(context.Chunks[0].Text, "违约责任");
        }
        finally
        {
            DeleteDir(indexDir);
        }
    }

    [TestMethod]
    public async Task RetrieveAsync_RerankJsonApplied_UsesModelOrder()
    {
        string indexDir = CreateTempDir();
        try
        {
            var app = new FakeWordApplication
            {
                ActiveDocument = FakeDocument.Create(
                    @"C:\docs\rerank.docx",
                    "rerank.docx",
                    new[]
                    {
                        BuildLongParagraph("第一章 总则", 130),
                        BuildLongParagraph("第二章 付款", 130),
                        BuildLongParagraph("第三章 违约责任", 130)
                    })
            };

            var model = new ReverseRerankModelService();
            var retriever = new HybridDocumentRetriever(
                new WordDocumentChunkProvider(app, null),
                new ZeroEmbeddingService(),
                new DocumentIndexStore(indexDir),
                model);

            RetrievedContext context = await retriever.RetrieveAsync(new DocumentQuery
            {
                QueryText = "概述合同章节",
                MaxChunks = 3,
                RerankCandidateCount = 3,
                MaxContextCharacters = 2600
            });

            Assert.IsFalse(string.IsNullOrWhiteSpace(model.ExpectedFirstChunkId));
            Assert.IsTrue(context.Chunks.Count > 0);
            Assert.AreEqual(model.ExpectedFirstChunkId, context.Chunks[0].ChunkId);
        }
        finally
        {
            DeleteDir(indexDir);
        }
    }

    [TestMethod]
    public async Task RetrieveAsync_ContextBudget_StopsAtConfiguredLimit()
    {
        string indexDir = CreateTempDir();
        try
        {
            var app = new FakeWordApplication
            {
                ActiveDocument = FakeDocument.Create(
                    @"C:\docs\budget.docx",
                    "budget.docx",
                    new[]
                    {
                        BuildLongParagraph("章节A", 70),
                        BuildLongParagraph("章节B", 70),
                        BuildLongParagraph("章节C", 70),
                        BuildLongParagraph("章节D", 70)
                    })
            };

            var retriever = new HybridDocumentRetriever(
                new WordDocumentChunkProvider(app, null),
                new ZeroEmbeddingService(),
                new DocumentIndexStore(indexDir),
                new NoopModelService());

            RetrievedContext context = await retriever.RetrieveAsync(new DocumentQuery
            {
                QueryText = "总结章节",
                MaxChunks = 5,
                MaxContextCharacters = 500,
                NeighborWindow = 0
            });

            Assert.IsTrue(context.CombinedText.Length <= 500);
        }
        finally
        {
            DeleteDir(indexDir);
        }
    }

    [TestMethod]
    public void CaptureSnapshot_HeadingStyle_ProducesHeadingChunkMetadata()
    {
        var app = new FakeWordApplication
        {
            ActiveDocument = FakeDocument.CreateStructured(
                @"C:\docs\heading.docx",
                "heading.docx",
                new[]
                {
                    new FakeParagraphSeed
                    {
                        Text = BuildLongParagraph("3.2 违约责任", 26),
                        Style = "Heading 2",
                        InTable = false
                    },
                    new FakeParagraphSeed
                    {
                        Text = BuildLongParagraph("违约金按合同执行", 24),
                        Style = "Normal",
                        InTable = false
                    }
                })
        };

        var provider = new WordDocumentChunkProvider(app, null);
        DocumentSnapshot snapshot = provider.CaptureSnapshot();

        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.Chunks.Count > 0);
        Assert.IsTrue(snapshot.Chunks.Any(chunk =>
            string.Equals(chunk.ChunkType, "Heading", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(chunk.HeadingPath) &&
            chunk.AuthorityScore >= 0.5d));
    }

    [TestMethod]
    public async Task RetrieveAsync_TableIntent_PrioritizesTableChunk()
    {
        string indexDir = CreateTempDir();
        try
        {
            var app = new FakeWordApplication
            {
                ActiveDocument = FakeDocument.CreateStructured(
                    @"C:\docs\table.docx",
                    "table.docx",
                    new[]
                    {
                        new FakeParagraphSeed
                        {
                            Text = BuildLongParagraph("交付时间 5天 说明", 24),
                            Style = "Normal",
                            InTable = false
                        },
                        new FakeParagraphSeed
                        {
                            Text = BuildLongParagraph("交付时间 3天 表格字段", 24),
                            Style = "Normal",
                            InTable = true
                        },
                        new FakeParagraphSeed
                        {
                            Text = BuildLongParagraph("付款流程", 24),
                            Style = "Normal",
                            InTable = false
                        }
                    })
            };

            var retriever = new HybridDocumentRetriever(
                new WordDocumentChunkProvider(app, null),
                new ZeroEmbeddingService(),
                new DocumentIndexStore(indexDir),
                new NoopModelService());

            RetrievedContext context = await retriever.RetrieveAsync(new DocumentQuery
            {
                QueryText = "请说明表格中交付时间",
                MaxChunks = 1,
                MaxContextCharacters = 1200
            });

            Assert.IsNotNull(context);
            Assert.IsTrue(context.Chunks.Count > 0);
            Assert.AreEqual("TableCell", context.Chunks[0].ChunkType);
        }
        finally
        {
            DeleteDir(indexDir);
        }
    }

    [TestMethod]
    public async Task RetrievalBenchmark_LargeDocument_FirstVsIncremental_ReportsLatencyAndEmbeddingCalls()
    {
        string indexDir = CreateTempDir();
        try
        {
            const int paragraphCount = 1200;
            var paragraphs = new List<string>(paragraphCount);
            for (int i = 0; i < paragraphCount; i++)
            {
                string section = i % 15 == 0 ? "违约责任" : "付款条款";
                paragraphs.Add(BuildLongParagraph("第" + i + "段 " + section, 22));
            }

            var app = new FakeWordApplication
            {
                ActiveDocument = FakeDocument.Create(
                    @"C:\docs\benchmark.docx",
                    "benchmark.docx",
                    paragraphs)
            };

            var embedding = new BenchmarkEmbeddingService();
            var retriever = new HybridDocumentRetriever(
                new WordDocumentChunkProvider(app, null),
                embedding,
                new DocumentIndexStore(indexDir),
                new NoopModelService());

            var firstWatch = Stopwatch.StartNew();
            RetrievedContext first = await retriever.RetrieveAsync(new DocumentQuery
            {
                QueryText = "请总结违约责任条款",
                MaxChunks = 5,
                Bm25CandidateCount = 48,
                DenseCandidateCount = 48,
                RerankCandidateCount = 24,
                MaxContextCharacters = 3200,
                NeighborWindow = 1
            });
            firstWatch.Stop();
            int firstSingleCalls = embedding.SingleInputCount;
            int firstBatchCalls = embedding.BatchInputCount;

            var secondWatch = Stopwatch.StartNew();
            RetrievedContext second = await retriever.RetrieveAsync(new DocumentQuery
            {
                QueryText = "请总结违约责任条款",
                MaxChunks = 5,
                Bm25CandidateCount = 48,
                DenseCandidateCount = 48,
                RerankCandidateCount = 24,
                MaxContextCharacters = 3200,
                NeighborWindow = 1
            });
            secondWatch.Stop();

            int secondSingleDelta = embedding.SingleInputCount - firstSingleCalls;
            int secondBatchDelta = embedding.BatchInputCount - firstBatchCalls;

            TestContext?.WriteLine(
                "[RetrievalBenchmark] Paragraphs={0}, FirstMs={1}, IncrementalMs={2}, FirstSingle={3}, FirstBatch={4}, IncrementalSingle={5}, IncrementalBatch={6}",
                paragraphCount,
                firstWatch.ElapsedMilliseconds,
                secondWatch.ElapsedMilliseconds,
                firstSingleCalls,
                firstBatchCalls,
                secondSingleDelta,
                secondBatchDelta);

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.IsTrue(first.Chunks.Count > 0);
            Assert.IsTrue(second.Chunks.Count > 0);
            Assert.IsTrue(firstBatchCalls > 0);
            Assert.AreEqual(0, secondBatchDelta);
            Assert.IsTrue(secondSingleDelta <= 2);
        }
        finally
        {
            DeleteDir(indexDir);
        }
    }

    private static string BuildLongParagraph(string title, int repeatCount)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < repeatCount; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(title);
        }

        return builder.ToString();
    }

    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "smartword-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDir(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private sealed class CountingEmbeddingService : IBatchEmbeddingService
    {
        public int TotalInputCount { get; private set; }

        public Task<float[]> CreateEmbeddingAsync(string input, string modelOverride, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalInputCount++;
            return Task.FromResult(BuildVector(input));
        }

        public Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(IReadOnlyList<string> inputs, string modelOverride, CancellationToken cancellationToken = default(CancellationToken))
        {
            var output = new List<float[]>();
            if (inputs == null)
            {
                return Task.FromResult((IReadOnlyList<float[]>)output);
            }

            for (int i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TotalInputCount++;
                output.Add(BuildVector(inputs[i]));
            }

            return Task.FromResult((IReadOnlyList<float[]>)output);
        }

        private static float[] BuildVector(string text)
        {
            float length = string.IsNullOrWhiteSpace(text) ? 0f : text.Length;
            return new[] { 1f, length, length % 7f };
        }
    }

    private sealed class BenchmarkEmbeddingService : IBatchEmbeddingService
    {
        public int SingleInputCount { get; private set; }

        public int BatchInputCount { get; private set; }

        public Task<float[]> CreateEmbeddingAsync(string input, string modelOverride, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SingleInputCount++;
            return Task.FromResult(BuildStableVector(input));
        }

        public Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(IReadOnlyList<string> inputs, string modelOverride, CancellationToken cancellationToken = default(CancellationToken))
        {
            var vectors = new List<float[]>();
            if (inputs == null)
            {
                return Task.FromResult((IReadOnlyList<float[]>)vectors);
            }

            BatchInputCount += inputs.Count;
            for (int i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                vectors.Add(BuildStableVector(inputs[i]));
            }

            return Task.FromResult((IReadOnlyList<float[]>)vectors);
        }

        private static float[] BuildStableVector(string text)
        {
            string value = text ?? string.Empty;
            int len = value.Length;
            int checksum = 0;
            for (int i = 0; i < value.Length; i++)
            {
                checksum += value[i];
            }

            return new[]
            {
                1f,
                (len % 251) / 251f,
                (checksum % 997) / 997f,
                ((len + checksum) % 409) / 409f
            };
        }
    }

    private sealed class ZeroEmbeddingService : IBatchEmbeddingService
    {
        public Task<float[]> CreateEmbeddingAsync(string input, string modelOverride, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(new float[] { 0f, 0f, 0f });
        }

        public Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(IReadOnlyList<string> inputs, string modelOverride, CancellationToken cancellationToken = default(CancellationToken))
        {
            var output = new List<float[]>();
            if (inputs != null)
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    output.Add(new float[] { 0f, 0f, 0f });
                }
            }

            return Task.FromResult((IReadOnlyList<float[]>)output);
        }
    }

    private sealed class NoopModelService : IModelService
    {
        public Task<string> RewriteTextAsync(EditorRewriteRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(string.Empty);
        }

        public Task<string> GenerateVbaCodeAsync(VbaGenerationRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(string.Empty);
        }

        public Task<string> ChatWithPromptsAsync(string systemPrompt, string userPrompt, string modelOverride, double temperature, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(string.Empty);
        }

        public Task<string> AnswerQuestionAsync(DocumentQaRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class ReverseRerankModelService : IModelService
    {
        public string ExpectedFirstChunkId { get; private set; }

        public Task<string> RewriteTextAsync(EditorRewriteRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(string.Empty);
        }

        public Task<string> GenerateVbaCodeAsync(VbaGenerationRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(string.Empty);
        }

        public Task<string> ChatWithPromptsAsync(string systemPrompt, string userPrompt, string modelOverride, double temperature, CancellationToken cancellationToken = default(CancellationToken))
        {
            MatchCollection matches = Regex.Matches(userPrompt ?? string.Empty, "\\b[a-z][a-z0-9_]*_[a-f0-9]{6,}\\b", RegexOptions.IgnoreCase);
            if (matches.Count == 0)
            {
                return Task.FromResult("{\"ordered_ids\":[]}");
            }

            var ids = new List<string>();
            for (int i = 0; i < matches.Count; i++)
            {
                string id = matches[i].Value;
                if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(id);
                }
            }

            ids.Reverse();
            ExpectedFirstChunkId = ids[0];
            return Task.FromResult("{\"ordered_ids\":[\"" + string.Join("\",\"", ids.ToArray()) + "\"]}");
        }

        public Task<string> AnswerQuestionAsync(DocumentQaRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(string.Empty);
        }
    }

    public sealed class FakeWordApplication
    {
        public FakeDocument ActiveDocument { get; set; }
    }

    public sealed class FakeDocument
    {
        public string FullName { get; set; }

        public string Name { get; set; }

        public FakeParagraphCollection Paragraphs { get; set; }

        public FakePropertyCollection CustomDocumentProperties { get; set; }

        public static FakeDocument Create(string fullName, string name, IEnumerable<string> paragraphs)
        {
            return new FakeDocument
            {
                FullName = fullName,
                Name = name,
                Paragraphs = new FakeParagraphCollection(paragraphs),
                CustomDocumentProperties = new FakePropertyCollection()
            };
        }

        public static FakeDocument CreateStructured(string fullName, string name, IEnumerable<FakeParagraphSeed> paragraphs)
        {
            return new FakeDocument
            {
                FullName = fullName,
                Name = name,
                Paragraphs = new FakeParagraphCollection(paragraphs),
                CustomDocumentProperties = new FakePropertyCollection()
            };
        }
    }

    public sealed class FakeParagraphCollection
    {
        private readonly List<FakeParagraph> _items;

        public FakeParagraphCollection(IEnumerable<string> paragraphs)
        {
            _items = new List<FakeParagraph>();
            if (paragraphs != null)
            {
                foreach (string paragraph in paragraphs)
                {
                    _items.Add(new FakeParagraph
                    {
                        Range = new FakeRange
                        {
                            Text = paragraph,
                            Style = "Normal",
                            Tables = new FakeTableCollection(0)
                        }
                    });
                }
            }
        }

        public FakeParagraphCollection(IEnumerable<FakeParagraphSeed> paragraphs)
        {
            _items = new List<FakeParagraph>();
            if (paragraphs != null)
            {
                foreach (FakeParagraphSeed paragraph in paragraphs)
                {
                    _items.Add(new FakeParagraph
                    {
                        Range = new FakeRange
                        {
                            Text = paragraph == null ? string.Empty : paragraph.Text,
                            Style = paragraph == null ? "Normal" : (paragraph.Style ?? "Normal"),
                            Tables = new FakeTableCollection(paragraph != null && paragraph.InTable ? 1 : 0)
                        }
                    });
                }
            }
        }

        public int Count
        {
            get { return _items.Count; }
        }

        public FakeParagraph this[int index]
        {
            get { return _items[index - 1]; }
        }

        public void Insert(int index, string paragraph)
        {
            _items.Insert(Math.Max(0, index - 1), new FakeParagraph
            {
                Range = new FakeRange
                {
                    Text = paragraph,
                    Style = "Normal",
                    Tables = new FakeTableCollection(0)
                }
            });
        }
    }

    public sealed class FakeParagraphSeed
    {
        public string Text { get; set; }

        public string Style { get; set; }

        public bool InTable { get; set; }
    }

    public sealed class FakeParagraph
    {
        public FakeRange Range { get; set; }
    }

    public sealed class FakeRange
    {
        public string Text { get; set; }

        public object Style { get; set; }

        public FakeTableCollection Tables { get; set; }
    }

    public sealed class FakeTableCollection
    {
        public FakeTableCollection(int count)
        {
            Count = count;
        }

        public int Count { get; set; }
    }

    public sealed class FakePropertyCollection
    {
        private readonly Dictionary<string, FakePropertyItem> _items = new Dictionary<string, FakePropertyItem>(StringComparer.OrdinalIgnoreCase);

        public FakePropertyItem this[string name]
        {
            get
            {
                FakePropertyItem item;
                if (!_items.TryGetValue(name, out item))
                {
                    throw new KeyNotFoundException(name);
                }

                return item;
            }
        }

        public void Add(string name, bool linkToContent, int type, object value)
        {
            _items[name] = new FakePropertyItem
            {
                Value = value == null ? string.Empty : value.ToString()
            };
        }
    }

    public sealed class FakePropertyItem
    {
        public string Value { get; set; }
    }
}
