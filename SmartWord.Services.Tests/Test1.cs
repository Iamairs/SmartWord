using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Core.Models;
using SmartWord.Core.Models.Conversation;
using SmartWord.Services.Conversation;
using SmartWord.Services.Logging;
using SmartWord.Services.Model;
using SmartWord.Services.Routing;
using SmartWord.Services.Vba;

namespace SmartWord.Services.Tests;

[TestClass]
public sealed class ConversationModeTests
{
    [TestMethod]
    public async Task DecideRouteAsync_QuestionIntent_ReturnsQaMode()
    {
        var service = new CommandRouteService(new LocalModelService());

        RouteDecision decision = await service.DecideRouteAsync(new RouteInput
        {
            UserMessage = "这段合同主要讲了什么？",
            SelectedText = string.Empty,
            RetrievedContext = string.Empty,
            ModelOverride = string.Empty
        });

        Assert.IsNotNull(decision);
        Assert.AreEqual(ConversationRouteType.Qa, decision.RouteType);
    }

    [TestMethod]
    public async Task DecideRouteAsync_ModeLockWriting_ReturnsWritingMode()
    {
        var service = new CommandRouteService(new LocalModelService());

        RouteDecision decision = await service.DecideRouteAsync(new RouteInput
        {
            UserMessage = "请把这段内容做成目录并优化表达",
            ModeLock = ConversationRouteType.Writing
        });

        Assert.IsNotNull(decision);
        Assert.AreEqual(ConversationRouteType.Writing, decision.RouteType);
        Assert.AreEqual("mode-lock", decision.ModeReasonCategory);
    }

    [TestMethod]
    public async Task RunTurnAsync_QaRoute_ReturnsAnswerWithoutPendingAction()
    {
        var store = new InMemoryConversationStore();
        var retriever = new FixedRetriever("p1", "[1] 这是合同总则", "p1");
        var routeService = new FixedRouteService(ConversationRouteType.Qa);
        var modelService = new FakeModelService
        {
            QaAnswer = "该段主要定义了双方的基本义务。"
        };
        var orchestrator = BuildOrchestrator(store, retriever, routeService, modelService, "");

        ChatTurnResult result = await orchestrator.RunTurnAsync(new ChatTurnRequest
        {
            UserMessage = "这段讲了什么？"
        });

        Assert.IsNotNull(result);
        Assert.AreEqual(ConversationRouteType.Qa, result.ResolvedMode);
        Assert.IsFalse(result.RequiresUserConfirmation);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.PendingActionId));
        StringAssert.Contains(result.AssistantReply ?? string.Empty, "双方的基本义务");
    }

    [TestMethod]
    public async Task RunTurnAsync_QaRoute_WritesCitationMetadataAndReadableReferences()
    {
        var store = new InMemoryConversationStore();
        var retriever = new FixedRetriever("doc-qa", "[8] 这是问答上下文", "c1_abc", 8, 10);
        var routeService = new FixedRouteService(ConversationRouteType.Qa);
        var modelService = new FakeModelService
        {
            QaAnswer = "这是基于引用片段生成的答案。"
        };
        var orchestrator = BuildOrchestrator(store, retriever, routeService, modelService, string.Empty);

        ChatTurnResult result = await orchestrator.RunTurnAsync(new ChatTurnRequest
        {
            UserMessage = "请总结研究目标。"
        });

        ConversationSession session = await store.GetSessionAsync(result.SessionId);
        Assert.IsNotNull(session);
        Assert.IsTrue(session.Messages.Count >= 2);

        ConversationMessage assistantMessage = session.Messages[session.Messages.Count - 1];
        StringAssert.Contains(assistantMessage.Content ?? string.Empty, "第8-10段");
        Assert.IsFalse((assistantMessage.Content ?? string.Empty).Contains("c1_abc"));
        StringAssert.Contains(assistantMessage.Metadata ?? string.Empty, "\"citations\"");
        StringAssert.Contains(assistantMessage.Metadata ?? string.Empty, "\"position\":8");
        StringAssert.Contains(assistantMessage.Metadata ?? string.Empty, "\"endPosition\":10");
    }

    [TestMethod]
    public async Task RunTurnAsync_QaRouteNoContext_ReturnsGuidanceMessage()
    {
        var store = new InMemoryConversationStore();
        var retriever = new FixedRetriever("doc-empty", string.Empty, string.Empty);
        var routeService = new FixedRouteService(ConversationRouteType.Qa);
        var modelService = new FakeModelService
        {
            QaAnswer = string.Empty
        };
        var orchestrator = BuildOrchestrator(store, retriever, routeService, modelService, string.Empty);

        ChatTurnResult result = await orchestrator.RunTurnAsync(new ChatTurnRequest
        {
            UserMessage = "这个条款的违约责任是什么？"
        });

        Assert.IsNotNull(result);
        Assert.IsFalse(result.RequiresUserConfirmation);
        StringAssert.Contains(result.AssistantReply ?? string.Empty, "未检索到足够文档依据");
    }

    [TestMethod]
    public async Task RunTurnAsync_WritingRoute_GeneratesPendingRewriteAction()
    {
        var store = new InMemoryConversationStore();
        var retriever = new FixedRetriever("doc-1", "[1] 原文片段", "p1");
        var routeService = new FixedRouteService(ConversationRouteType.Writing);
        var modelService = new FakeModelService
        {
            RewriteText = "改写后的文本"
        };
        var orchestrator = BuildOrchestrator(store, retriever, routeService, modelService, "原文");

        ChatTurnResult result = await orchestrator.RunTurnAsync(new ChatTurnRequest
        {
            UserMessage = "请优化这段话"
        });

        Assert.IsNotNull(result);
        Assert.AreEqual(ConversationRouteType.Writing, result.ResolvedMode);
        Assert.IsTrue(result.RequiresUserConfirmation);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.PendingActionId));
    }

    [TestMethod]
    public async Task RunTurnAsync_WritingRoute_DoesNotTriggerRetrieval()
    {
        var store = new InMemoryConversationStore();
        var retriever = new CountingRetriever("doc-1", "[1] 原文片段", "p1");
        var routeService = new FixedRouteService(ConversationRouteType.Writing);
        var modelService = new FakeModelService
        {
            RewriteText = "改写后的文本"
        };
        var orchestrator = BuildOrchestrator(store, retriever, routeService, modelService, "原文");

        ChatTurnResult result = await orchestrator.RunTurnAsync(new ChatTurnRequest
        {
            UserMessage = "请优化这段话"
        });

        Assert.IsNotNull(result);
        Assert.AreEqual(ConversationRouteType.Writing, result.ResolvedMode);
        Assert.AreEqual(0, retriever.CallCount);
    }

    [TestMethod]
    public async Task RunTurnAsync_QaRoute_TriggersRetrievalOnce()
    {
        var store = new InMemoryConversationStore();
        var retriever = new CountingRetriever("doc-qa", "[1] QA 上下文", "p1");
        var routeService = new FixedRouteService(ConversationRouteType.Qa);
        var modelService = new FakeModelService
        {
            QaAnswer = "这是基于上下文的答案。"
        };
        var orchestrator = BuildOrchestrator(store, retriever, routeService, modelService, string.Empty);

        ChatTurnResult result = await orchestrator.RunTurnAsync(new ChatTurnRequest
        {
            UserMessage = "这段内容说了什么？"
        });

        Assert.IsNotNull(result);
        Assert.AreEqual(ConversationRouteType.Qa, result.ResolvedMode);
        Assert.AreEqual(1, retriever.CallCount);
    }

    [TestMethod]
    public async Task RunTurnAsync_WritingRoute_Cancelled_ShouldNotPersistTurnArtifacts()
    {
        var store = new InMemoryConversationStore();
        var retriever = new FixedRetriever("doc-1", "[1] 原文片段", "p1");
        var routeService = new FixedRouteService(ConversationRouteType.Writing);
        var modelService = new DelayedRewriteModelService();
        var orchestrator = BuildOrchestrator(store, retriever, routeService, modelService, "原文");

        using (var cts = new CancellationTokenSource())
        {
            Task<ChatTurnResult> task = orchestrator.RunTurnAsync(new ChatTurnRequest
            {
                UserMessage = "请优化这段话"
            }, cts.Token);

            cts.CancelAfter(60);
            bool cancelled = false;
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert.IsTrue(cancelled);
        }

        IReadOnlyList<ConversationSession> sessions = await store.LoadSessionsAsync();
        Assert.AreEqual(1, sessions.Count);
        Assert.AreEqual(0, sessions[0].Messages.Count);
        Assert.AreEqual(0, sessions[0].PendingActions.Count);
    }

    private static ConversationOrchestrator BuildOrchestrator(
        IConversationStore store,
        IDocumentRetriever retriever,
        ICommandRouteService routeService,
        IModelService modelService,
        string selectedText)
    {
        return new ConversationOrchestrator(
            store,
            retriever,
            routeService,
            new FixedSelectionService(selectedText),
            modelService,
            new VbaCodeSanitizer(),
            new NoopVbaExecutor(),
            new NoopNotificationService(),
            NullAppLogger.Instance);
    }

    private sealed class FixedRouteService : ICommandRouteService
    {
        private readonly ConversationRouteType _routeType;

        public FixedRouteService(ConversationRouteType routeType)
        {
            _routeType = routeType;
        }

        public Task<RouteDecision> DecideRouteAsync(RouteInput input, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(new RouteDecision
            {
                RouteType = _routeType,
                Confidence = 0.9d,
                Reason = "test",
                ModeReasonCategory = "test"
            });
        }
    }

    private sealed class FixedRetriever : IDocumentRetriever
    {
        private readonly string _documentId;
        private readonly string _combinedText;
        private readonly string _chunkId;
        private readonly int _position;
        private readonly int _endPosition;

        public FixedRetriever(string documentId, string combinedText, string chunkId, int position = 1, int endPosition = 1)
        {
            _documentId = documentId;
            _combinedText = combinedText;
            _chunkId = chunkId;
            _position = position;
            _endPosition = endPosition <= 0 ? position : endPosition;
        }

        public Task<RetrievedContext> RetrieveAsync(DocumentQuery query, CancellationToken cancellationToken = default(CancellationToken))
        {
            var context = new RetrievedContext
            {
                DocumentId = _documentId,
                CombinedText = _combinedText
            };

            if (!string.IsNullOrWhiteSpace(_chunkId))
            {
                context.Chunks.Add(new RetrievedChunk
                {
                    ChunkId = _chunkId,
                    Text = _combinedText,
                    Position = _position,
                    EndPosition = _endPosition,
                    Score = 0.8d
                });
            }

            return Task.FromResult(context);
        }
    }

    private sealed class CountingRetriever : IDocumentRetriever
    {
        private readonly FixedRetriever _inner;

        public CountingRetriever(string documentId, string combinedText, string chunkId)
        {
            _inner = new FixedRetriever(documentId, combinedText, chunkId);
        }

        public int CallCount { get; private set; }

        public async Task<RetrievedContext> RetrieveAsync(DocumentQuery query, CancellationToken cancellationToken = default(CancellationToken))
        {
            CallCount++;
            return await _inner.RetrieveAsync(query, cancellationToken);
        }
    }

    private sealed class FixedSelectionService : ISelectionService
    {
        private readonly string _selectedText;

        public FixedSelectionService(string selectedText)
        {
            _selectedText = selectedText;
        }

        public string GetSelectedText()
        {
            return _selectedText;
        }

        public void ReplaceSelection(string newText)
        {
        }

        public void SelectParagraphRange(int startParagraphIndex, int endParagraphIndex)
        {
        }
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public void Error(string message)
        {
        }

        public void Info(string message)
        {
        }

        public void Success(string message)
        {
        }

        public void Warn(string message)
        {
        }
    }

    private sealed class NoopVbaExecutor : IVbaExecutor
    {
        public void Execute(string vbaCode, string entryPoint)
        {
        }
    }

    private sealed class FakeModelService : IModelService
    {
        public string RewriteText { get; set; }

        public string QaAnswer { get; set; }

        public Task<string> RewriteTextAsync(EditorRewriteRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(RewriteText ?? string.Empty);
        }

        public Task<string> GenerateVbaCodeAsync(VbaGenerationRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult("Public Sub SmartWord_Run()\r\nEnd Sub");
        }

        public Task<string> ChatWithPromptsAsync(string systemPrompt, string userPrompt, string modelOverride, double temperature, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult("{}");
        }

        public Task<string> AnswerQuestionAsync(DocumentQaRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(QaAnswer ?? string.Empty);
        }
    }

    private sealed class DelayedRewriteModelService : IModelService
    {
        public async Task<string> RewriteTextAsync(EditorRewriteRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            await Task.Delay(500, cancellationToken);
            return "延迟改写结果";
        }

        public Task<string> GenerateVbaCodeAsync(VbaGenerationRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(string.Empty);
        }

        public Task<string> ChatWithPromptsAsync(string systemPrompt, string userPrompt, string modelOverride, double temperature, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult("{}");
        }

        public Task<string> AnswerQuestionAsync(DocumentQaRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class InMemoryConversationStore : IConversationStore
    {
        private readonly List<ConversationSession> _sessions = new List<ConversationSession>();
        private string _activeSessionId;

        public Task<IReadOnlyList<ConversationSession>> LoadSessionsAsync()
        {
            return Task.FromResult((IReadOnlyList<ConversationSession>)new List<ConversationSession>(_sessions));
        }

        public Task<ConversationSession> CreateSessionAsync(string title)
        {
            var session = new ConversationSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                Title = string.IsNullOrWhiteSpace(title) ? "新对话" : title,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            for (int i = 0; i < _sessions.Count; i++)
            {
                _sessions[i].IsActive = false;
            }

            _activeSessionId = session.SessionId;
            _sessions.Insert(0, session);
            return Task.FromResult(session);
        }

        public Task<ConversationSession> GetSessionAsync(string sessionId)
        {
            for (int i = 0; i < _sessions.Count; i++)
            {
                if (string.Equals(_sessions[i].SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(_sessions[i]);
                }
            }

            return Task.FromResult<ConversationSession>(null);
        }

        public Task<ConversationSession> GetActiveSessionAsync()
        {
            return GetSessionAsync(_activeSessionId);
        }

        public Task SetActiveSessionAsync(string sessionId)
        {
            _activeSessionId = sessionId;
            for (int i = 0; i < _sessions.Count; i++)
            {
                _sessions[i].IsActive = string.Equals(_sessions[i].SessionId, sessionId, StringComparison.OrdinalIgnoreCase);
            }

            return Task.CompletedTask;
        }

        public Task SaveSessionAsync(ConversationSession session)
        {
            if (session == null)
            {
                return Task.CompletedTask;
            }

            for (int i = 0; i < _sessions.Count; i++)
            {
                if (string.Equals(_sessions[i].SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
                {
                    _sessions[i] = session;
                    return Task.CompletedTask;
                }
            }

            _sessions.Insert(0, session);
            return Task.CompletedTask;
        }
    }
}
