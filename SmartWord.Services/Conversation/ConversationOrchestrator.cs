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

namespace SmartWord.Services.Conversation
{
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

        public Task<IReadOnlyList<ConversationSession>> LoadSessionsAsync()
        {
            return _conversationStore.LoadSessionsAsync();
        }

        public Task<ConversationSession> CreateSessionAsync(string title)
        {
            return _conversationStore.CreateSessionAsync(title);
        }

        public Task SetActiveSessionAsync(string sessionId)
        {
            return _conversationStore.SetActiveSessionAsync(sessionId);
        }

        public async Task<ChatTurnResult> RunTurnAsync(ChatTurnRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.UserMessage))
            {
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
                session = await _conversationStore.CreateSessionAsync("新对话").ConfigureAwait(false);
            }

            string selectedText = _selectionService == null ? string.Empty : _selectionService.GetSelectedText();
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

            return await _conversationStore.GetActiveSessionAsync().ConfigureAwait(false);
        }

        private async Task<PendingAction> BuildPendingActionAsync(
            RouteDecision route,
            ChatTurnRequest request,
            string selectedText,
            RetrievedContext retrieved)
        {
            if (route == null)
            {
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

        private static string BuildMergedInstruction(string userMessage, RetrievedContext retrieved)
        {
            string context = retrieved == null ? string.Empty : retrieved.CombinedText;
            if (string.IsNullOrWhiteSpace(context))
            {
                return userMessage ?? string.Empty;
            }

            return (userMessage ?? string.Empty) + "\n\n参考上下文：\n" + context;
        }

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

                string safeCode = _vbaCodeSanitizer.SanitizeAndValidate(action.VbaCode, action.EntryPoint);
                _vbaExecutor.Execute(safeCode, action.EntryPoint);
            }
        }

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
