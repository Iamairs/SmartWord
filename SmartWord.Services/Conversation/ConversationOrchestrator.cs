using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Core.Models;
using SmartWord.Core.Models.Conversation;
using SmartWord.Core.Orchestration.Conversation;
using SmartWord.Services.Logging;
using SmartWord.Services.Vba;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// 文件说明：
// 对话编排核心实现，负责会话轮次执行、路由分流、待执行动作生成与确认执行。
namespace SmartWord.Services.Conversation
{
    /// <summary>
    /// 会话编排器实现。
    /// </summary>
    public sealed class ConversationOrchestrator : IConversationOrchestrator
    {
        private readonly IConversationStore _conversationStore;
        private readonly IDocumentRetriever _documentRetriever;
        private readonly ICommandRouteService _commandRouteService;
        private readonly ISelectionService _selectionService;
        private readonly IModelService _modelService;
        private readonly VbaCodeSanitizer _vbaCodeSanitizer;
        private readonly IVbaExecutor _vbaExecutor;
        private readonly INotificationService _notificationService;
        private readonly IAppLogger _logger;

        /// <summary>
        /// 初始化会话编排器。
        /// </summary>
        /// <param name="conversationStore">会话存储。</param>
        /// <param name="documentRetriever">文档检索器。</param>
        /// <param name="commandRouteService">路由服务。</param>
        /// <param name="selectionService">选区服务。</param>
        /// <param name="modelService">模型服务。</param>
        /// <param name="vbaCodeSanitizer">VBA 代码净化器。</param>
        /// <param name="vbaExecutor">VBA 执行器。</param>
        /// <param name="notificationService">通知服务。</param>
        public ConversationOrchestrator(
            IConversationStore conversationStore,
            IDocumentRetriever documentRetriever,
            ICommandRouteService commandRouteService,
            ISelectionService selectionService,
            IModelService modelService,
            VbaCodeSanitizer vbaCodeSanitizer,
            IVbaExecutor vbaExecutor,
            INotificationService notificationService,
            IAppLogger logger)
        {
            _conversationStore = conversationStore;
            _documentRetriever = documentRetriever;
            _commandRouteService = commandRouteService;
            _selectionService = selectionService;
            _modelService = modelService;
            _vbaCodeSanitizer = vbaCodeSanitizer;
            _vbaExecutor = vbaExecutor;
            _notificationService = notificationService;
            _logger = logger ?? NullAppLogger.Instance;
        }

        /// <summary>
        /// 加载会话列表。
        /// </summary>
        /// <returns>会话只读列表。</returns>
        public Task<IReadOnlyList<ConversationSession>> LoadSessionsAsync()
        {
            return _conversationStore.LoadSessionsAsync();
        }

        /// <summary>
        /// 创建新会话。
        /// </summary>
        /// <param name="title">会话标题。</param>
        /// <returns>新建会话。</returns>
        public Task<ConversationSession> CreateSessionAsync(string title)
        {
            return _conversationStore.CreateSessionAsync(title);
        }

        /// <summary>
        /// 设置活动会话。
        /// </summary>
        /// <param name="sessionId">目标会话 ID。</param>
        public Task SetActiveSessionAsync(string sessionId)
        {
            return _conversationStore.SetActiveSessionAsync(sessionId);
        }

        /// <summary>
        /// 执行一轮对话：检索上下文、路由判定、生成建议并写入会话历史。
        /// </summary>
        /// <param name="request">对话轮次请求。</param>
        /// <returns>轮次结果。</returns>
        public async Task<ChatTurnResult> RunTurnAsync(ChatTurnRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null || string.IsNullOrWhiteSpace(request.UserMessage))
            {
                // 输入为空时返回可展示结果，避免上层额外判空分支。
                return new ChatTurnResult
                {
                    AssistantReply = "请输入问题后再发送。",
                    RequiresUserConfirmation = false,
                    RouteType = ConversationRouteType.Writing,
                    ResolvedMode = ConversationRouteType.Writing
                };
            }

            cancellationToken.ThrowIfCancellationRequested();

            string correlationId = Guid.NewGuid().ToString("N");
            var stopwatch = Stopwatch.StartNew();
            using (_logger.BeginScope("CorrelationId", correlationId))
            {
                _logger.Info(
                    "chat.turn.start",
                    "RunTurn start. SessionId={SessionId} UserMessage={UserMessage} ModelOverride={ModelOverride} PromptVersion={PromptVersion} ModeLock={ModeLock}",
                    request.SessionId,
                    request.UserMessage,
                    request.ModelOverride,
                    request.PromptVersion,
                    request.ModeLock.HasValue ? request.ModeLock.Value.ToString() : "auto");

                try
                {
                    ConversationSession session = await ResolveSessionAsync(request.SessionId).ConfigureAwait(false);
                    if (session == null)
                    {
                        // 会话不存在时自动创建新会话，确保流程可继续。
                        session = await _conversationStore.CreateSessionAsync("新对话").ConfigureAwait(false);
                    }

                    using (_logger.BeginScope("SessionId", session.SessionId))
                    {
                        string selectedText = _selectionService == null ? string.Empty : _selectionService.GetSelectedText();
                        // 先完成模式判定；仅当问答模式时再执行检索，避免非问答场景产生额外检索成本。
                        RouteDecision route = await _commandRouteService.DecideRouteAsync(new RouteInput
                        {
                            UserMessage = request.UserMessage,
                            SelectedText = selectedText,
                            RetrievedContext = string.Empty,
                            ModelOverride = request.ModelOverride,
                            ModeLock = request.ModeLock
                        }, cancellationToken).ConfigureAwait(false);

                        ConversationRouteType resolvedMode = NormalizeRouteType(route == null ? ConversationRouteType.Writing : route.RouteType);
                        _logger.Info(
                            "chat.route.decided",
                            "Route decided. RouteType={RouteType} ResolvedMode={ResolvedMode} Confidence={Confidence} Reason={Reason} Category={Category}",
                            route == null ? ConversationRouteType.Writing.ToString() : route.RouteType.ToString(),
                            resolvedMode.ToString(),
                            route == null ? 0d : route.Confidence,
                            route == null ? string.Empty : route.Reason,
                            route == null ? string.Empty : (route.ModeReasonCategory ?? string.Empty));

                        RetrievedContext retrieved = null;
                        if (resolvedMode == ConversationRouteType.Qa)
                        {
                            _logger.Info("chat.retrieval.start", "Retrieval start for QA mode. SessionId={SessionId}", session.SessionId);
                            retrieved = await _documentRetriever.RetrieveAsync(new DocumentQuery
                            {
                                QueryText = request.UserMessage,
                                SelectedText = selectedText,
                                MaxChunks = 5,
                                ModelOverride = request.ModelOverride
                            }, cancellationToken).ConfigureAwait(false);
                            _logger.Info(
                                "chat.retrieval.end",
                                "Retrieval completed for QA mode. SessionId={SessionId} ChunkCount={ChunkCount}",
                                session.SessionId,
                                retrieved == null || retrieved.Chunks == null ? 0 : retrieved.Chunks.Count);
                        }

                        PendingAction pendingAction = null;
                        string assistantReply;

                        if (resolvedMode == ConversationRouteType.Qa)
                        {
                            assistantReply = await BuildQuestionAnswerReplyAsync(route, request, selectedText, retrieved, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // 非问答模式统一走“先建议后执行”流程。
                            pendingAction = await BuildPendingActionAsync(resolvedMode, route, request, selectedText, retrieved, cancellationToken).ConfigureAwait(false);
                            assistantReply = BuildAssistantReply(resolvedMode, route, pendingAction, selectedText);
                        }

                        // 取消请求到达后不写入本轮消息与待执行动作，避免污染会话历史。
                        cancellationToken.ThrowIfCancellationRequested();

                        session.Messages.Add(new ConversationMessage
                        {
                            Role = "user",
                            Content = request.UserMessage,
                            TimestampUtc = DateTime.UtcNow,
                            Metadata = "{}"
                        });

                        session.Messages.Add(new ConversationMessage
                        {
                            Role = "assistant",
                            Content = assistantReply,
                            TimestampUtc = DateTime.UtcNow,
                            Metadata = "{}"
                        });

                        if (pendingAction != null)
                        {
                            // 将待执行动作持久化到会话，供用户确认执行。
                            session.PendingActions.Add(pendingAction);
                        }

                        session.IsActive = true;
                        session.UpdatedAtUtc = DateTime.UtcNow;
                        await _conversationStore.SaveSessionAsync(session).ConfigureAwait(false);
                        await _conversationStore.SetActiveSessionAsync(session.SessionId).ConfigureAwait(false);

                        ChatTurnResult result = new ChatTurnResult
                        {
                            SessionId = session.SessionId,
                            AssistantReply = assistantReply,
                            PendingActionId = pendingAction == null ? string.Empty : pendingAction.ActionId,
                            RequiresUserConfirmation = pendingAction != null,
                            RouteType = resolvedMode,
                            ResolvedMode = resolvedMode
                        };
                        stopwatch.Stop();
                        _logger.Info(
                            "chat.turn.end",
                            "RunTurn completed. SessionId={SessionId} RouteType={RouteType} RequiresUserConfirmation={RequiresUserConfirmation} DurationMs={DurationMs}",
                            result.SessionId,
                            result.RouteType.ToString(),
                            result.RequiresUserConfirmation,
                            stopwatch.ElapsedMilliseconds);

                        return result;
                    }
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    _logger.Warn(
                        "chat.turn.cancelled",
                        "RunTurn cancelled. SessionId={SessionId} UserMessage={UserMessage} DurationMs={DurationMs}",
                        request.SessionId,
                        request.UserMessage,
                        stopwatch.ElapsedMilliseconds);
                    throw;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    _logger.Error(
                        "chat.turn.failed",
                        ex,
                        "RunTurn failed. SessionId={SessionId} UserMessage={UserMessage} DurationMs={DurationMs}",
                        request.SessionId,
                        request.UserMessage,
                        stopwatch.ElapsedMilliseconds);
                    throw;
                }
            }
        }

        /// <summary>
        /// 应用指定待执行动作。
        /// </summary>
        /// <param name="sessionId">会话 ID。</param>
        /// <param name="actionId">动作 ID。</param>
        /// <returns>动作执行结果。</returns>
        public async Task<ApplyActionResult> ApplyPendingActionAsync(string sessionId, string actionId)
        {
            var stopwatch = Stopwatch.StartNew();
            using (_logger.BeginScope("SessionId", sessionId))
            using (_logger.BeginScope("ActionId", actionId))
            {
                _logger.Info("chat.action.apply.start", "Apply action start. SessionId={SessionId} ActionId={ActionId}", sessionId, actionId);
                ConversationSession session = await _conversationStore.GetSessionAsync(sessionId).ConfigureAwait(false);
                if (session == null)
                {
                    stopwatch.Stop();
                    _logger.Warn("chat.action.apply.not-found-session", "Apply action failed because session was not found. SessionId={SessionId} DurationMs={DurationMs}", sessionId, stopwatch.ElapsedMilliseconds);
                    return new ApplyActionResult
                    {
                        SessionId = sessionId,
                        ActionId = actionId,
                        Success = false,
                        Message = "未找到会话。"
                    };
                }

                PendingAction action = FindPendingAction(session, actionId);
                if (action == null)
                {
                    stopwatch.Stop();
                    _logger.Warn("chat.action.apply.not-found-action", "Apply action failed because pending action was not found. SessionId={SessionId} ActionId={ActionId} DurationMs={DurationMs}", session.SessionId, actionId, stopwatch.ElapsedMilliseconds);
                    return new ApplyActionResult
                    {
                        SessionId = session.SessionId,
                        ActionId = actionId,
                        Success = false,
                        Message = "未找到待执行动作。"
                    };
                }

                if (action.IsApplied)
                {
                    stopwatch.Stop();
                    _logger.Warn("chat.action.apply.already-applied", "Apply action skipped because action already applied. SessionId={SessionId} ActionId={ActionId} DurationMs={DurationMs}", session.SessionId, action.ActionId, stopwatch.ElapsedMilliseconds);
                    return new ApplyActionResult
                    {
                        SessionId = session.SessionId,
                        ActionId = action.ActionId,
                        Success = false,
                        Message = "该动作已执行。"
                    };
                }

                try
                {
                    // 执行成功后打已应用标记，避免重复执行同一动作。
                    ExecuteAction(action);
                    action.IsApplied = true;

                    string resultMessage = "已执行建议动作。";
                    session.Messages.Add(new ConversationMessage
                    {
                        Role = "assistant",
                        Content = resultMessage,
                        TimestampUtc = DateTime.UtcNow,
                        Metadata = "{\"type\":\"apply\"}"
                    });

                    session.UpdatedAtUtc = DateTime.UtcNow;
                    await _conversationStore.SaveSessionAsync(session).ConfigureAwait(false);
                    stopwatch.Stop();
                    _logger.Info("chat.action.apply.end", "Apply action completed. SessionId={SessionId} ActionId={ActionId} DurationMs={DurationMs}", session.SessionId, action.ActionId, stopwatch.ElapsedMilliseconds);

                    return new ApplyActionResult
                    {
                        SessionId = session.SessionId,
                        ActionId = action.ActionId,
                        Success = true,
                        Message = resultMessage
                    };
                }
                catch (Exception ex)
                {
                    string error = "执行失败：" + ex.Message;
                    _notificationService.Error(error);
                    stopwatch.Stop();
                    _logger.Error("chat.action.apply.failed", ex, "Apply action failed. SessionId={SessionId} ActionId={ActionId} DurationMs={DurationMs}", session.SessionId, action.ActionId, stopwatch.ElapsedMilliseconds);

                    session.Messages.Add(new ConversationMessage
                    {
                        Role = "assistant",
                        Content = error,
                        TimestampUtc = DateTime.UtcNow,
                        Metadata = "{\"type\":\"error\"}"
                    });
                    session.UpdatedAtUtc = DateTime.UtcNow;
                    await _conversationStore.SaveSessionAsync(session).ConfigureAwait(false);

                    return new ApplyActionResult
                    {
                        SessionId = session.SessionId,
                        ActionId = action.ActionId,
                        Success = false,
                        Message = error
                    };
                }
            }
        }

        /// <summary>
        /// 解析本轮应使用的会话。
        /// </summary>
        /// <param name="sessionId">请求中的会话 ID。</param>
        /// <returns>命中会话；未命中时返回活动会话或空值。</returns>
        private async Task<ConversationSession> ResolveSessionAsync(string sessionId)
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                ConversationSession byId = await _conversationStore.GetSessionAsync(sessionId).ConfigureAwait(false);
                if (byId != null)
                {
                    return byId;
                }
            }

            // 显式会话不存在时回退到当前活动会话。
            return await _conversationStore.GetActiveSessionAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 按路由生成待执行动作。
        /// </summary>
        /// <param name="resolvedMode">归一化后的模式。</param>
        /// <param name="route">路由决策。</param>
        /// <param name="request">对话轮次请求。</param>
        /// <param name="selectedText">当前选区文本。</param>
        /// <param name="retrieved">检索上下文。</param>
        /// <returns>待执行动作；无有效载荷时返回空值。</returns>
        private async Task<PendingAction> BuildPendingActionAsync(
            ConversationRouteType resolvedMode,
            RouteDecision route,
            ChatTurnRequest request,
            string selectedText,
            RetrievedContext retrieved,
            CancellationToken cancellationToken)
        {
            if (resolvedMode == ConversationRouteType.Qa)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            string mergedInstruction = BuildMergedInstruction(request.UserMessage, retrieved);
            string rewriteSource = string.IsNullOrWhiteSpace(selectedText)
                ? (retrieved == null ? string.Empty : retrieved.CombinedText)
                : selectedText;

            var action = new PendingAction
            {
                ActionId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow,
                EntryPoint = "SmartWord_Run",
                RouteType = resolvedMode,
                ActionType = ConversationActionType.None
            };

            bool shouldGenerateRewrite = resolvedMode == ConversationRouteType.Writing || resolvedMode == ConversationRouteType.Processing;
            bool shouldGenerateVba = resolvedMode == ConversationRouteType.Execute;

            // 执行模式下若同时包含明显改写意图，则生成混合动作。
            if (resolvedMode == ConversationRouteType.Execute && IsExecuteHybridInstruction(request.UserMessage))
            {
                shouldGenerateRewrite = !string.IsNullOrWhiteSpace(rewriteSource);
            }

            if (shouldGenerateRewrite && !string.IsNullOrWhiteSpace(rewriteSource))
            {
                string rewriteInstruction = mergedInstruction;
                if (resolvedMode == ConversationRouteType.Processing)
                {
                    rewriteInstruction = "请按结构化处理方式完成以下任务：\n" + mergedInstruction;
                }

                action.RewriteText = await _modelService.RewriteTextAsync(new EditorRewriteRequest
                {
                    Instruction = rewriteInstruction,
                    SelectedText = rewriteSource,
                    ModelOverride = request.ModelOverride,
                    PromptVersion = request.PromptVersion
                }, cancellationToken).ConfigureAwait(false);
            }

            if (shouldGenerateVba)
            {
                action.VbaCode = await _modelService.GenerateVbaCodeAsync(new VbaGenerationRequest
                {
                    Instruction = mergedInstruction,
                    SelectedText = selectedText,
                    ModelOverride = request.ModelOverride,
                    PromptVersion = request.PromptVersion,
                    EntryPoint = "SmartWord_Run"
                }, cancellationToken).ConfigureAwait(false);
            }

            bool hasRewrite = !string.IsNullOrWhiteSpace(action.RewriteText);
            bool hasVba = !string.IsNullOrWhiteSpace(action.VbaCode);
            if (hasRewrite && hasVba)
            {
                action.ActionType = ConversationActionType.Hybrid;
            }
            else if (hasVba)
            {
                action.ActionType = ConversationActionType.Vba;
            }
            else if (hasRewrite)
            {
                action.ActionType = ConversationActionType.Rewrite;
            }
            else
            {
                action.ActionType = ConversationActionType.None;
            }

            if (action.ActionType == ConversationActionType.None)
            {
                return null;
            }

            return action;
        }

        /// <summary>
        /// 构建文档问答回复。
        /// </summary>
        /// <param name="route">路由决策。</param>
        /// <param name="request">对话轮次请求。</param>
        /// <param name="selectedText">当前选区文本。</param>
        /// <param name="retrieved">检索上下文。</param>
        /// <returns>问答回复文本。</returns>
        private async Task<string> BuildQuestionAnswerReplyAsync(
            RouteDecision route,
            ChatTurnRequest request,
            string selectedText,
            RetrievedContext retrieved,
            CancellationToken cancellationToken)
        {
            string retrievedContext = retrieved == null ? string.Empty : retrieved.CombinedText;
            if (string.IsNullOrWhiteSpace(retrievedContext))
            {
                _logger.Warn("qa.no-context", "No retrieved context for QA. Question={Question}", request.UserMessage);
            }

            _logger.Info("qa.request.start", "QA request start. Question={Question}", request.UserMessage);
            string answer = await _modelService.AnswerQuestionAsync(new DocumentQaRequest
            {
                Question = request.UserMessage,
                SelectedText = selectedText,
                RetrievedContext = retrievedContext,
                ModelOverride = request.ModelOverride,
                PromptVersion = request.PromptVersion
            }, cancellationToken).ConfigureAwait(false);
            _logger.Info("qa.request.end", "QA request completed. AnswerLength={AnswerLength}", string.IsNullOrWhiteSpace(answer) ? 0 : answer.Length);

            if (string.IsNullOrWhiteSpace(answer))
            {
                if (string.IsNullOrWhiteSpace(retrievedContext))
                {
                    return "未检索到足够文档依据，建议缩小问题范围或先选中相关段落后重试。";
                }

                return "暂时无法生成稳定答案，请调整提问后重试。";
            }

            var builder = new StringBuilder();
            builder.Append("模式：").Append(RouteTypeToText(ConversationRouteType.Qa));
            if (!string.IsNullOrWhiteSpace(route == null ? null : route.Reason))
            {
                builder.Append("（").Append(route.Reason).Append("）");
            }

            builder.AppendLine();
            builder.AppendLine(answer.Trim());

            string refs = BuildChunkReferences(retrieved);
            if (!string.IsNullOrWhiteSpace(refs))
            {
                builder.AppendLine();
                builder.Append("参考片段：").Append(refs);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 将用户指令与检索上下文合并为统一提示文本。
        /// </summary>
        /// <param name="userMessage">用户指令。</param>
        /// <param name="retrieved">检索上下文。</param>
        /// <returns>合并后的指令文本。</returns>
        private static string BuildMergedInstruction(string userMessage, RetrievedContext retrieved)
        {
            string context = retrieved == null ? string.Empty : retrieved.CombinedText;
            if (string.IsNullOrWhiteSpace(context))
            {
                return userMessage ?? string.Empty;
            }

            return (userMessage ?? string.Empty) + "\n\n参考上下文：\n" + context;
        }

        /// <summary>
        /// 生成展示给用户的建议回复文本。
        /// </summary>
        /// <param name="resolvedMode">归一化后的模式。</param>
        /// <param name="route">路由决策。</param>
        /// <param name="action">待执行动作。</param>
        /// <param name="selectedText">当前选区文本。</param>
        /// <returns>回复文本。</returns>
        private static string BuildAssistantReply(ConversationRouteType resolvedMode, RouteDecision route, PendingAction action, string selectedText)
        {
            if (action == null)
            {
                return "我暂时无法生成可执行建议，请检查指令或先选中文本后重试。";
            }

            var builder = new StringBuilder();
            builder.Append("模式：").Append(RouteTypeToText(resolvedMode));

            if (!string.IsNullOrWhiteSpace(route == null ? null : route.Reason))
            {
                builder.Append("（").Append(route.Reason).Append("）");
            }

            builder.AppendLine();

            if (!string.IsNullOrWhiteSpace(action.RewriteText))
            {
                builder.AppendLine("建议内容预览：");
                builder.AppendLine(TrimForPreview(action.RewriteText, 600));
            }
            else if ((resolvedMode == ConversationRouteType.Writing || resolvedMode == ConversationRouteType.Processing) && string.IsNullOrWhiteSpace(selectedText))
            {
                builder.AppendLine("未检测到选中文本，确认执行前请先选中要替换的内容。\n");
            }

            if (!string.IsNullOrWhiteSpace(action.VbaCode))
            {
                builder.AppendLine("已生成执行脚本（预览）：");
                builder.AppendLine("```vba");
                builder.AppendLine(TrimForPreview(action.VbaCode, 3000));
                builder.AppendLine("```");
            }

            builder.Append("点击“确认执行”后才会修改文档。\n");
            return builder.ToString();
        }

        /// <summary>
        /// 将路由枚举转换为中文展示文案。
        /// </summary>
        /// <param name="routeType">路由类型。</param>
        /// <returns>展示文案。</returns>
        private static string RouteTypeToText(ConversationRouteType routeType)
        {
            if (routeType == ConversationRouteType.Qa)
            {
                return "文档问答";
            }

            if (routeType == ConversationRouteType.Processing)
            {
                return "结构化处理";
            }

            if (routeType == ConversationRouteType.Execute || routeType == ConversationRouteType.Vba || routeType == ConversationRouteType.Hybrid)
            {
                return "执行操作";
            }

            return "写作改写";
        }

        /// <summary>
        /// 执行待执行动作（改写替换和/或 VBA 执行）。
        /// </summary>
        /// <param name="action">待执行动作。</param>
        private void ExecuteAction(PendingAction action)
        {
            if (action.ActionType == ConversationActionType.Rewrite || action.ActionType == ConversationActionType.Hybrid)
            {
                if (string.IsNullOrWhiteSpace(action.RewriteText))
                {
                    throw new InvalidOperationException("改写结果为空，无法执行替换。");
                }

                _logger.Info("chat.action.execute.rewrite", "Applying rewrite action. ActionId={ActionId} RewriteLength={RewriteLength}", action.ActionId, action.RewriteText.Length);
                _selectionService.ReplaceSelection(action.RewriteText);
            }

            if (action.ActionType == ConversationActionType.Vba || action.ActionType == ConversationActionType.Hybrid)
            {
                if (string.IsNullOrWhiteSpace(action.VbaCode))
                {
                    throw new InvalidOperationException("VBA 代码为空，无法执行排版。");
                }

                // 执行前进行代码净化与入口校验，避免注入非法脚本。
                string safeCode = _vbaCodeSanitizer.SanitizeAndValidate(action.VbaCode, action.EntryPoint);
                _logger.Info("chat.action.execute.vba", "Executing VBA action. ActionId={ActionId} EntryPoint={EntryPoint} VbaLength={VbaLength}", action.ActionId, action.EntryPoint, safeCode.Length);
                _vbaExecutor.Execute(safeCode, action.EntryPoint);
            }
        }

        /// <summary>
        /// 按动作 ID 从会话中查找待执行动作。
        /// </summary>
        /// <param name="session">会话对象。</param>
        /// <param name="actionId">动作 ID。</param>
        /// <returns>命中的动作；未命中返回空值。</returns>
        private static PendingAction FindPendingAction(ConversationSession session, string actionId)
        {
            if (session == null || session.PendingActions == null || string.IsNullOrWhiteSpace(actionId))
            {
                return null;
            }

            for (int i = 0; i < session.PendingActions.Count; i++)
            {
                if (string.Equals(session.PendingActions[i].ActionId, actionId, StringComparison.OrdinalIgnoreCase))
                {
                    return session.PendingActions[i];
                }
            }

            return null;
        }

        /// <summary>
        /// 判断执行模式下是否需要生成混合动作。
        /// </summary>
        /// <param name="instruction">用户指令。</param>
        /// <returns>是否同时包含写作意图。</returns>
        private static bool IsExecuteHybridInstruction(string instruction)
        {
            string text = instruction ?? string.Empty;
            return Regex.IsMatch(text, "润色|改写|优化|重写|rewrite|polish", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 根据检索结果生成参考片段标识串。
        /// </summary>
        /// <param name="retrieved">检索上下文。</param>
        /// <returns>参考片段标识。</returns>
        private static string BuildChunkReferences(RetrievedContext retrieved)
        {
            if (retrieved == null || retrieved.Chunks == null || retrieved.Chunks.Count == 0)
            {
                return string.Empty;
            }

            var refs = new List<string>();
            int maxCount = Math.Min(3, retrieved.Chunks.Count);
            for (int i = 0; i < maxCount; i++)
            {
                RetrievedChunk chunk = retrieved.Chunks[i];
                if (chunk == null || string.IsNullOrWhiteSpace(chunk.ChunkId))
                {
                    continue;
                }

                refs.Add(chunk.ChunkId);
            }

            return refs.Count == 0 ? string.Empty : string.Join(",", refs.ToArray());
        }

        /// <summary>
        /// 将历史兼容路由值归一化为新模式。
        /// </summary>
        /// <param name="routeType">原始路由类型。</param>
        /// <returns>归一化后的路由类型。</returns>
        private static ConversationRouteType NormalizeRouteType(ConversationRouteType routeType)
        {
            if (routeType == ConversationRouteType.Rewrite)
            {
                return ConversationRouteType.Writing;
            }

            if (routeType == ConversationRouteType.Vba || routeType == ConversationRouteType.Hybrid)
            {
                return ConversationRouteType.Execute;
            }

            return routeType;
        }

        /// <summary>
        /// 对长文本做预览截断。
        /// </summary>
        /// <param name="input">输入文本。</param>
        /// <param name="maxLength">最大预览长度。</param>
        /// <returns>截断后的预览文本。</returns>
        private static string TrimForPreview(string input, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            string value = input.Trim();
            if (value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }
    }
}
