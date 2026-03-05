using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Core.Models;
using SmartWord.Core.Models.Conversation;
using SmartWord.Core.Orchestration.Conversation;
using SmartWord.Services.Vba;
using System;
using System.Collections.Generic;
using System.Text;
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
            INotificationService notificationService)
        {
            _conversationStore = conversationStore;
            _documentRetriever = documentRetriever;
            _commandRouteService = commandRouteService;
            _selectionService = selectionService;
            _modelService = modelService;
            _vbaCodeSanitizer = vbaCodeSanitizer;
            _vbaExecutor = vbaExecutor;
            _notificationService = notificationService;
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
        public async Task<ChatTurnResult> RunTurnAsync(ChatTurnRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.UserMessage))
            {
                // 输入为空时返回可展示结果，避免上层额外判空分支。
                return new ChatTurnResult
                {
                    AssistantReply = "请输入问题后再发送。",
                    RequiresUserConfirmation = false,
                    RouteType = ConversationRouteType.Rewrite
                };
            }

            ConversationSession session = await ResolveSessionAsync(request.SessionId).ConfigureAwait(false);
            if (session == null)
            {
                // 会话不存在时自动创建新会话，确保流程可继续。
                session = await _conversationStore.CreateSessionAsync("新对话").ConfigureAwait(false);
            }

            string selectedText = _selectionService == null ? string.Empty : _selectionService.GetSelectedText();
            // 对当前文档做检索增强，为后续路由与生成提供上下文。
            RetrievedContext retrieved = await _documentRetriever.RetrieveAsync(new DocumentQuery
            {
                QueryText = request.UserMessage,
                SelectedText = selectedText,
                MaxChunks = 5,
                ModelOverride = request.ModelOverride
            }).ConfigureAwait(false);

            RouteDecision route = await _commandRouteService.DecideRouteAsync(new RouteInput
            {
                UserMessage = request.UserMessage,
                SelectedText = selectedText,
                RetrievedContext = retrieved == null ? string.Empty : retrieved.CombinedText,
                ModelOverride = request.ModelOverride
            }).ConfigureAwait(false);

            // 基于路由生成待执行动作，但不立即修改文档，保持“先建议后执行”。
            PendingAction pendingAction = await BuildPendingActionAsync(route, request, selectedText, retrieved).ConfigureAwait(false);
            string assistantReply = BuildAssistantReply(route, pendingAction, selectedText);

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

            return new ChatTurnResult
            {
                SessionId = session.SessionId,
                AssistantReply = assistantReply,
                PendingActionId = pendingAction == null ? string.Empty : pendingAction.ActionId,
                RequiresUserConfirmation = pendingAction != null,
                RouteType = route == null ? ConversationRouteType.Rewrite : route.RouteType
            };
        }

        /// <summary>
        /// 应用指定待执行动作。
        /// </summary>
        /// <param name="sessionId">会话 ID。</param>
        /// <param name="actionId">动作 ID。</param>
        /// <returns>动作执行结果。</returns>
        public async Task<ApplyActionResult> ApplyPendingActionAsync(string sessionId, string actionId)
        {
            ConversationSession session = await _conversationStore.GetSessionAsync(sessionId).ConfigureAwait(false);
            if (session == null)
            {
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
        /// <param name="route">路由决策。</param>
        /// <param name="request">对话轮次请求。</param>
        /// <param name="selectedText">当前选区文本。</param>
        /// <param name="retrieved">检索上下文。</param>
        /// <returns>待执行动作；无有效载荷时返回空值。</returns>
        private async Task<PendingAction> BuildPendingActionAsync(
            RouteDecision route,
            ChatTurnRequest request,
            string selectedText,
            RetrievedContext retrieved)
        {
            if (route == null)
            {
                // 路由异常时默认走改写，避免中断主流程。
                route = new RouteDecision
                {
                    RouteType = ConversationRouteType.Rewrite,
                    Confidence = 0.5d,
                    Reason = "默认路由"
                };
            }

            string mergedInstruction = BuildMergedInstruction(request.UserMessage, retrieved);
            string rewriteSource = string.IsNullOrWhiteSpace(selectedText)
                ? (retrieved == null ? string.Empty : retrieved.CombinedText)
                : selectedText;

            var action = new PendingAction
            {
                ActionId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.UtcNow,
                EntryPoint = "SmartWord_Run",
                RouteType = route.RouteType,
                ActionType = ConversationActionType.None
            };

            if (route.RouteType == ConversationRouteType.Rewrite || route.RouteType == ConversationRouteType.Hybrid)
            {
                if (!string.IsNullOrWhiteSpace(rewriteSource))
                {
                    action.RewriteText = await _modelService.RewriteTextAsync(new EditorRewriteRequest
                    {
                        Instruction = mergedInstruction,
                        SelectedText = rewriteSource,
                        ModelOverride = request.ModelOverride,
                        PromptVersion = request.PromptVersion
                    }).ConfigureAwait(false);
                }
            }

            if (route.RouteType == ConversationRouteType.Vba || route.RouteType == ConversationRouteType.Hybrid)
            {
                action.VbaCode = await _modelService.GenerateVbaCodeAsync(new VbaGenerationRequest
                {
                    Instruction = mergedInstruction,
                    SelectedText = selectedText,
                    ModelOverride = request.ModelOverride,
                    PromptVersion = request.PromptVersion,
                    EntryPoint = "SmartWord_Run"
                }).ConfigureAwait(false);
            }

            if (route.RouteType == ConversationRouteType.Hybrid)
            {
                action.ActionType = ConversationActionType.Hybrid;
            }
            else if (route.RouteType == ConversationRouteType.Vba)
            {
                action.ActionType = ConversationActionType.Vba;
            }
            else
            {
                action.ActionType = ConversationActionType.Rewrite;
            }

            bool hasPayload = !string.IsNullOrWhiteSpace(action.RewriteText) || !string.IsNullOrWhiteSpace(action.VbaCode);
            if (!hasPayload)
            {
                return null;
            }

            return action;
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
        /// <param name="route">路由决策。</param>
        /// <param name="action">待执行动作。</param>
        /// <param name="selectedText">当前选区文本。</param>
        /// <returns>回复文本。</returns>
        private static string BuildAssistantReply(RouteDecision route, PendingAction action, string selectedText)
        {
            if (action == null)
            {
                return "我暂时无法生成可执行建议，请检查指令或先选中文本后重试。";
            }

            var builder = new StringBuilder();
            builder.Append("路由结果：").Append(RouteTypeToText(route == null ? ConversationRouteType.Rewrite : route.RouteType));

            if (!string.IsNullOrWhiteSpace(route == null ? null : route.Reason))
            {
                builder.Append("（").Append(route.Reason).Append("）");
            }

            builder.AppendLine();

            if (!string.IsNullOrWhiteSpace(action.RewriteText))
            {
                builder.AppendLine("建议改写如下：");
                builder.AppendLine(TrimForPreview(action.RewriteText, 600));
            }
            else if ((route != null && route.RouteType != ConversationRouteType.Vba) && string.IsNullOrWhiteSpace(selectedText))
            {
                builder.AppendLine("未检测到选中文本，确认执行前请先选中要替换的内容。");
            }

            if (!string.IsNullOrWhiteSpace(action.VbaCode))
            {
                builder.AppendLine("已生成排版脚本（预览）：");
                builder.AppendLine(TrimForPreview(action.VbaCode, 300));
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
            if (routeType == ConversationRouteType.Vba)
            {
                return "排版脚本";
            }

            if (routeType == ConversationRouteType.Hybrid)
            {
                return "改写 + 排版";
            }

            return "文本改写";
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
