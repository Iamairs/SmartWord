using SmartWord.Core.Models.Conversation;
using SmartWord.Core.Orchestration.Conversation;
using SmartWord.Core.Abstractions;
using SmartWord.Services.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SmartWord.AddIn.UI.Web
{
    // 文件说明：
    // WebView2 与会话编排器之间的 JSON RPC 桥接层，负责协议解析、方法分发与统一错误封装。
    internal sealed class WebViewRpcBridge
    {
        private const string ProtocolVersion = "1.0";
        private readonly IConversationOrchestrator _conversationOrchestrator;
        private readonly string[] _availableModels;
        private readonly string _defaultModel;
        private readonly string _defaultPromptVersion;
        private readonly IAppLogger _logger;
        private readonly JavaScriptSerializer _serializer;
        private readonly ConcurrentDictionary<string, InflightTurnState> _inflightTurns;

        /// <summary>
        /// 初始化 RPC 桥接器。
        /// </summary>
        public WebViewRpcBridge(
            IConversationOrchestrator conversationOrchestrator,
            string[] availableModels,
            string defaultModel,
            string defaultPromptVersion,
            IAppLogger logger)
        {
            _conversationOrchestrator = conversationOrchestrator;
            _availableModels = availableModels ?? new string[0];
            _defaultModel = defaultModel ?? string.Empty;
            _defaultPromptVersion = defaultPromptVersion ?? string.Empty;
            _logger = logger ?? NullAppLogger.Instance;
            _serializer = new JavaScriptSerializer();
            _inflightTurns = new ConcurrentDictionary<string, InflightTurnState>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 处理来自前端的 JSON RPC 请求并返回响应 JSON。
        /// </summary>
        public async Task<string> HandleAsync(string requestJson)
        {
            string requestId = string.Empty;
            string method = string.Empty;
            try
            {
                Dictionary<string, object> request = DeserializeObject(requestJson);
                requestId = ReadString(request, "requestId");
                method = ReadString(request, "method");
                Dictionary<string, object> payload = ReadDictionary(request, "payload");

                if (string.IsNullOrWhiteSpace(requestId))
                {
                    requestId = Guid.NewGuid().ToString("N");
                }

                if (string.IsNullOrWhiteSpace(method))
                {
                    return BuildErrorResponse(
                        requestId,
                        "invalid-request",
                        "RPC method is required.",
                        string.Empty);
                }

                _logger.Info("ui.rpc.request", "RPC request received. Method={Method} RequestId={RequestId}", method, requestId);
                object responsePayload = await DispatchAsync(method, payload).ConfigureAwait(false);
                _logger.Info("ui.rpc.response", "RPC request completed. Method={Method} RequestId={RequestId}", method, requestId);
                return BuildSuccessResponse(requestId, responsePayload);
            }
            catch (Exception ex)
            {
                _logger.Error("ui.rpc.failed", ex, "RPC request failed. Method={Method} RequestId={RequestId}", method, requestId);
                return BuildErrorResponse(
                    string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId,
                    "rpc-failed",
                    ex.Message,
                    ex.GetType().Name);
            }
        }

        /// <summary>
        /// 按方法名路由请求。
        /// </summary>
        private async Task<object> DispatchAsync(string method, Dictionary<string, object> payload)
        {
            string normalized = (method ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "app.getconfig")
            {
                return BuildConfigPayload();
            }

            if (normalized == "sessions.load")
            {
                return await BuildSessionsPayloadAsync(string.Empty).ConfigureAwait(false);
            }

            if (normalized == "sessions.create")
            {
                ConversationSession session = await _conversationOrchestrator.CreateSessionAsync("新对话").ConfigureAwait(false);
                return await BuildSessionsPayloadAsync(session == null ? string.Empty : session.SessionId).ConfigureAwait(false);
            }

            if (normalized == "sessions.activate")
            {
                string sessionId = ReadString(payload, "sessionId");
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    throw new InvalidOperationException("缺少 sessionId 参数。");
                }

                await _conversationOrchestrator.SetActiveSessionAsync(sessionId).ConfigureAwait(false);
                return await BuildSessionsPayloadAsync(sessionId).ConfigureAwait(false);
            }

            if (normalized == "turn.submit")
            {
                return await HandleSubmitTurnAsync(payload).ConfigureAwait(false);
            }

            if (normalized == "turn.cancel")
            {
                return await HandleCancelTurnAsync(payload).ConfigureAwait(false);
            }

            if (normalized == "action.apply")
            {
                return await HandleApplyActionAsync(payload).ConfigureAwait(false);
            }

            if (normalized == "action.cancellocal")
            {
                return new Dictionary<string, object>
                {
                    { "message", "已取消待执行动作。" }
                };
            }

            if (normalized == "app.ready" || normalized == "ui.focusinput")
            {
                return new Dictionary<string, object>
                {
                    { "ok", true }
                };
            }

            throw new InvalidOperationException("未知 RPC 方法：" + method);
        }

        /// <summary>
        /// 处理发送消息请求。
        /// </summary>
        private async Task<object> HandleSubmitTurnAsync(Dictionary<string, object> payload)
        {
            string userMessage = ReadString(payload, "userMessage");
            if (string.IsNullOrWhiteSpace(userMessage))
            {
                throw new InvalidOperationException("消息内容不能为空。");
            }

            string sessionId = ReadString(payload, "sessionId");
            string turnId = ReadString(payload, "turnId");
            if (string.IsNullOrWhiteSpace(turnId))
            {
                turnId = Guid.NewGuid().ToString("N");
            }

            var request = new ChatTurnRequest
            {
                SessionId = sessionId,
                UserMessage = userMessage,
                ModelOverride = ReadString(payload, "modelOverride"),
                PromptVersion = ReadString(payload, "promptVersion"),
                ModeLock = ParseModeLock(ReadString(payload, "modeLock"))
            };

            var inflight = new InflightTurnState(sessionId, new CancellationTokenSource());
            if (!_inflightTurns.TryAdd(turnId, inflight))
            {
                throw new InvalidOperationException("turnId 冲突，请重试。" + turnId);
            }

            try
            {
                inflight.TurnTask = _conversationOrchestrator.RunTurnAsync(request, inflight.CancellationTokenSource.Token);
                ChatTurnResult result = await inflight.TurnTask.ConfigureAwait(false);
                if (result != null)
                {
                    result.TurnId = turnId;
                }

                Dictionary<string, object> sessionsPayload = await BuildSessionsPayloadAsync(result == null ? string.Empty : result.SessionId).ConfigureAwait(false);
                Dictionary<string, object> pendingActionMeta = ReadDictionary(sessionsPayload, "pendingActionMeta");
                Dictionary<string, object> uiHints = ReadDictionary(sessionsPayload, "uiHints");
                sessionsPayload["result"] = BuildTurnResult(result, pendingActionMeta, uiHints);
                return sessionsPayload;
            }
            catch (OperationCanceledException)
            {
                Dictionary<string, object> sessionsPayload = await BuildSessionsPayloadAsync(sessionId).ConfigureAwait(false);
                sessionsPayload["result"] = BuildCancelledTurnResult(sessionId, turnId, "已取消本轮生成。");
                return sessionsPayload;
            }
            finally
            {
                InflightTurnState removed;
                _inflightTurns.TryRemove(turnId, out removed);
                if (removed != null)
                {
                    removed.CancellationTokenSource.Dispose();
                }
            }
        }

        /// <summary>
        /// 取消进行中的发送轮次。
        /// </summary>
        private async Task<object> HandleCancelTurnAsync(Dictionary<string, object> payload)
        {
            string turnId = ReadString(payload, "turnId");
            string sessionId = ReadString(payload, "sessionId");

            InflightTurnState inflight = null;
            if (!string.IsNullOrWhiteSpace(turnId))
            {
                _inflightTurns.TryGetValue(turnId, out inflight);
            }

            if (inflight == null && !string.IsNullOrWhiteSpace(sessionId))
            {
                foreach (KeyValuePair<string, InflightTurnState> pair in _inflightTurns)
                {
                    InflightTurnState candidate = pair.Value;
                    if (candidate != null && string.Equals(candidate.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        turnId = pair.Key;
                        inflight = candidate;
                        break;
                    }
                }
            }

            if (inflight == null)
            {
                return new Dictionary<string, object>
                {
                    { "cancelled", false },
                    { "turnId", turnId ?? string.Empty },
                    { "message", "未找到进行中的生成任务。" }
                };
            }

            inflight.CancellationTokenSource.Cancel();
            Task<ChatTurnResult> runningTask = inflight.TurnTask;
            if (runningTask != null)
            {
                try
                {
                    await runningTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            return new Dictionary<string, object>
            {
                { "cancelled", true },
                { "turnId", turnId ?? string.Empty },
                { "message", "已取消本轮生成。" }
            };
        }

        /// <summary>
        /// 处理确认执行请求。
        /// </summary>
        private async Task<object> HandleApplyActionAsync(Dictionary<string, object> payload)
        {
            string sessionId = ReadString(payload, "sessionId");
            string actionId = ReadString(payload, "actionId");
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(actionId))
            {
                throw new InvalidOperationException("缺少 sessionId 或 actionId 参数。");
            }

            ApplyActionResult result = await _conversationOrchestrator.ApplyPendingActionAsync(sessionId, actionId).ConfigureAwait(false);
            Dictionary<string, object> sessionsPayload = await BuildSessionsPayloadAsync(sessionId).ConfigureAwait(false);
            Dictionary<string, object> pendingActionMeta = ReadDictionary(sessionsPayload, "pendingActionMeta");
            Dictionary<string, object> uiHints = ReadDictionary(sessionsPayload, "uiHints");
            sessionsPayload["result"] = BuildApplyResult(result, pendingActionMeta, uiHints);
            return sessionsPayload;
        }

        /// <summary>
        /// 构建会话数据响应。
        /// </summary>
        private async Task<Dictionary<string, object>> BuildSessionsPayloadAsync(string preferredSessionId)
        {
            IReadOnlyList<ConversationSession> sessions = await _conversationOrchestrator.LoadSessionsAsync().ConfigureAwait(false);
            string activeSessionId = ResolveActiveSessionId(sessions, preferredSessionId);
            ConversationSession activeSession = FindSessionById(sessions, activeSessionId);
            PendingAction pendingAction = FindLatestPendingAction(activeSession);
            Dictionary<string, object> pendingActionMeta = BuildPendingActionMeta(pendingAction);
            Dictionary<string, object> uiHints = BuildUiHints(
                pendingAction == null ? string.Empty : pendingAction.ActionId,
                pendingActionMeta,
                pendingAction != null);

            return new Dictionary<string, object>
            {
                { "sessions", BuildSessionDtos(sessions) },
                { "activeSessionId", activeSessionId },
                { "pendingActionMeta", pendingActionMeta },
                { "uiHints", uiHints }
            };
        }

        /// <summary>
        /// 构建前端初始化配置。
        /// </summary>
        private object BuildConfigPayload()
        {
            return new Dictionary<string, object>
            {
                { "availableModels", _availableModels },
                { "defaultModel", _defaultModel },
                { "defaultPromptVersion", _defaultPromptVersion },
                { "modeOptions", new object[]
                    {
                        new Dictionary<string, object> { { "key", string.Empty }, { "label", "自动" } },
                        new Dictionary<string, object> { { "key", "qa" }, { "label", "问答" } },
                        new Dictionary<string, object> { { "key", "writing" }, { "label", "写作" } },
                        new Dictionary<string, object> { { "key", "processing" }, { "label", "处理" } },
                        new Dictionary<string, object> { { "key", "execute" }, { "label", "执行" } }
                    }
                }
            };
        }

        /// <summary>
        /// 将会话实体映射为前端 DTO。
        /// </summary>
        private static object[] BuildSessionDtos(IReadOnlyList<ConversationSession> sessions)
        {
            if (sessions == null || sessions.Count == 0)
            {
                return new object[0];
            }

            var list = new List<object>(sessions.Count);
            for (int i = 0; i < sessions.Count; i++)
            {
                ConversationSession session = sessions[i];
                list.Add(new Dictionary<string, object>
                {
                    { "sessionId", session == null ? string.Empty : (session.SessionId ?? string.Empty) },
                    { "title", session == null ? string.Empty : (session.Title ?? string.Empty) },
                    { "isActive", session != null && session.IsActive },
                    { "updatedAtUtc", session == null ? string.Empty : session.UpdatedAtUtc.ToString("o", CultureInfo.InvariantCulture) },
                    { "messages", BuildMessageDtos(session == null ? null : session.Messages) },
                    { "latestPendingAction", BuildPendingActionMeta(FindLatestPendingAction(session)) }
                });
            }

            return list.ToArray();
        }

        /// <summary>
        /// 将消息实体映射为前端 DTO。
        /// </summary>
        private static object[] BuildMessageDtos(IList<ConversationMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return new object[0];
            }

            var list = new List<object>(messages.Count);
            for (int i = 0; i < messages.Count; i++)
            {
                ConversationMessage message = messages[i];
                list.Add(new Dictionary<string, object>
                {
                    { "role", message == null ? string.Empty : (message.Role ?? string.Empty) },
                    { "content", message == null ? string.Empty : (message.Content ?? string.Empty) },
                    { "timestampUtc", message == null ? string.Empty : message.TimestampUtc.ToString("o", CultureInfo.InvariantCulture) }
                });
            }

            return list.ToArray();
        }

        /// <summary>
        /// 构建轮次结果 DTO。
        /// </summary>
        private static object BuildTurnResult(
            ChatTurnResult result,
            Dictionary<string, object> pendingActionMeta,
            Dictionary<string, object> uiHints)
        {
            if (result == null)
            {
                return new Dictionary<string, object>();
            }

            return new Dictionary<string, object>
            {
                { "sessionId", result.SessionId ?? string.Empty },
                { "turnId", result.TurnId ?? string.Empty },
                { "assistantReply", result.AssistantReply ?? string.Empty },
                { "pendingActionId", result.PendingActionId ?? string.Empty },
                { "requiresUserConfirmation", result.RequiresUserConfirmation },
                { "resolvedMode", RouteToModeKey(result.ResolvedMode) },
                { "pendingActionMeta", pendingActionMeta },
                { "uiHints", uiHints }
            };
        }

        /// <summary>
        /// 构建取消轮次结果 DTO。
        /// </summary>
        private static object BuildCancelledTurnResult(string sessionId, string turnId, string message)
        {
            return new Dictionary<string, object>
            {
                { "sessionId", sessionId ?? string.Empty },
                { "turnId", turnId ?? string.Empty },
                { "cancelled", true },
                { "message", message ?? "已取消本轮生成。" },
                { "assistantReply", string.Empty },
                { "pendingActionId", string.Empty },
                { "requiresUserConfirmation", false },
                { "resolvedMode", "writing" }
            };
        }

        /// <summary>
        /// 构建动作应用结果 DTO。
        /// </summary>
        private static object BuildApplyResult(
            ApplyActionResult result,
            Dictionary<string, object> pendingActionMeta,
            Dictionary<string, object> uiHints)
        {
            if (result == null)
            {
                return new Dictionary<string, object>();
            }

            return new Dictionary<string, object>
            {
                { "sessionId", result.SessionId ?? string.Empty },
                { "actionId", result.ActionId ?? string.Empty },
                { "success", result.Success },
                { "message", result.Message ?? string.Empty },
                { "pendingActionMeta", pendingActionMeta },
                { "uiHints", uiHints }
            };
        }

        /// <summary>
        /// 查找会话对象。
        /// </summary>
        private static ConversationSession FindSessionById(IReadOnlyList<ConversationSession> sessions, string sessionId)
        {
            if (sessions == null || sessions.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    if (sessions[i] != null && string.Equals(sessions[i].SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        return sessions[i];
                    }
                }
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i] != null && sessions[i].IsActive)
                {
                    return sessions[i];
                }
            }

            return sessions[0];
        }

        /// <summary>
        /// 获取最近未应用的待执行动作。
        /// </summary>
        private static PendingAction FindLatestPendingAction(ConversationSession session)
        {
            if (session == null || session.PendingActions == null || session.PendingActions.Count == 0)
            {
                return null;
            }

            PendingAction latest = null;
            for (int i = 0; i < session.PendingActions.Count; i++)
            {
                PendingAction action = session.PendingActions[i];
                if (action == null || action.IsApplied)
                {
                    continue;
                }

                if (latest == null || action.CreatedAtUtc > latest.CreatedAtUtc)
                {
                    latest = action;
                }
            }

            return latest;
        }

        /// <summary>
        /// 构建待执行动作元数据。
        /// </summary>
        private static Dictionary<string, object> BuildPendingActionMeta(PendingAction action)
        {
            if (action == null)
            {
                return new Dictionary<string, object>();
            }

            string actionType = ActionTypeToKey(action.ActionType);
            string route = RouteToModeKey(action.RouteType);
            string targetScope = (action.ActionType == ConversationActionType.Vba || action.ActionType == ConversationActionType.Hybrid)
                ? "document"
                : "selection";

            string summary = BuildActionSummary(action);
            string riskLevel = EstimateRiskLevel(action);

            return new Dictionary<string, object>
            {
                { "actionId", action.ActionId ?? string.Empty },
                { "actionType", actionType },
                { "routeType", route },
                { "targetScope", targetScope },
                { "summary", summary },
                { "riskLevel", riskLevel },
                { "entryPoint", action.EntryPoint ?? string.Empty }
            };
        }

        /// <summary>
        /// 构建界面提示信息。
        /// </summary>
        private static Dictionary<string, object> BuildUiHints(
            string pendingActionId,
            Dictionary<string, object> pendingActionMeta,
            bool requiresUserConfirmation)
        {
            bool hasPending = !string.IsNullOrWhiteSpace(pendingActionId);
            string riskLevel = ReadString(pendingActionMeta, "riskLevel");
            string warningText = string.Empty;

            if (!hasPending)
            {
                warningText = "当前无待执行动作，可继续提问或下达新指令。";
            }
            else if (string.Equals(riskLevel, "high", StringComparison.OrdinalIgnoreCase))
            {
                warningText = "本次操作影响范围较大，请先核对建议内容后再执行。";
            }

            return new Dictionary<string, object>
            {
                { "canApply", hasPending && requiresUserConfirmation },
                { "canCancel", hasPending },
                { "warningText", warningText },
                { "checks", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "key", "pending" },
                            { "label", "存在待执行动作" },
                            { "passed", hasPending }
                        },
                        new Dictionary<string, object>
                        {
                            { "key", "confirm" },
                            { "label", "当前轮次需要确认" },
                            { "passed", hasPending && requiresUserConfirmation }
                        },
                        new Dictionary<string, object>
                        {
                            { "key", "risk" },
                            { "label", "已评估执行风险" },
                            { "passed", hasPending ? !string.IsNullOrWhiteSpace(riskLevel) : true }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// 动作摘要文本。
        /// </summary>
        private static string BuildActionSummary(PendingAction action)
        {
            if (action == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(action.RewriteText))
            {
                string text = action.RewriteText.Trim();
                if (text.Length > 96)
                {
                    text = text.Substring(0, 96) + "...";
                }

                return "建议改写预览：" + text;
            }

            if (!string.IsNullOrWhiteSpace(action.VbaCode))
            {
                var summary = new StringBuilder();
                summary.Append("将执行 VBA 脚本");
                if (!string.IsNullOrWhiteSpace(action.EntryPoint))
                {
                    summary.Append("（入口：").Append(action.EntryPoint).Append("）");
                }

                return summary.ToString();
            }

            return "已生成可执行建议。";
        }

        /// <summary>
        /// 估算风险级别。
        /// </summary>
        private static string EstimateRiskLevel(PendingAction action)
        {
            if (action == null)
            {
                return "none";
            }

            if (action.ActionType == ConversationActionType.Vba || action.ActionType == ConversationActionType.Hybrid)
            {
                return "high";
            }

            if (!string.IsNullOrWhiteSpace(action.RewriteText) && action.RewriteText.Length > 1200)
            {
                return "medium";
            }

            return "low";
        }

        /// <summary>
        /// 动作类型映射。
        /// </summary>
        private static string ActionTypeToKey(ConversationActionType actionType)
        {
            if (actionType == ConversationActionType.Vba)
            {
                return "vba";
            }

            if (actionType == ConversationActionType.Hybrid)
            {
                return "hybrid";
            }

            if (actionType == ConversationActionType.Rewrite)
            {
                return "rewrite";
            }

            return "none";
        }

        /// <summary>
        /// 解析模式锁定值。
        /// </summary>
        private static ConversationRouteType? ParseModeLock(string mode)
        {
            string value = (mode ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "qa")
            {
                return ConversationRouteType.Qa;
            }

            if (value == "writing" || value == "rewrite")
            {
                return ConversationRouteType.Writing;
            }

            if (value == "processing")
            {
                return ConversationRouteType.Processing;
            }

            if (value == "execute" || value == "vba" || value == "hybrid")
            {
                return ConversationRouteType.Execute;
            }

            return null;
        }

        /// <summary>
        /// 将路由值映射到前端模式键。
        /// </summary>
        private static string RouteToModeKey(ConversationRouteType routeType)
        {
            if (routeType == ConversationRouteType.Qa)
            {
                return "qa";
            }

            if (routeType == ConversationRouteType.Processing)
            {
                return "processing";
            }

            if (routeType == ConversationRouteType.Execute ||
                routeType == ConversationRouteType.Vba ||
                routeType == ConversationRouteType.Hybrid)
            {
                return "execute";
            }

            return "writing";
        }

        /// <summary>
        /// 进行中轮次状态。
        /// </summary>
        private sealed class InflightTurnState
        {
            public InflightTurnState(string sessionId, CancellationTokenSource cancellationTokenSource)
            {
                SessionId = sessionId ?? string.Empty;
                CancellationTokenSource = cancellationTokenSource ?? throw new ArgumentNullException("cancellationTokenSource");
            }

            public string SessionId { get; private set; }

            public CancellationTokenSource CancellationTokenSource { get; private set; }

            public Task<ChatTurnResult> TurnTask { get; set; }
        }

        /// <summary>
        /// 解析活动会话 ID。
        /// </summary>
        private static string ResolveActiveSessionId(IReadOnlyList<ConversationSession> sessions, string preferredSessionId)
        {
            if (!string.IsNullOrWhiteSpace(preferredSessionId))
            {
                for (int i = 0; i < (sessions == null ? 0 : sessions.Count); i++)
                {
                    if (sessions[i] != null && string.Equals(sessions[i].SessionId, preferredSessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        return preferredSessionId;
                    }
                }
            }

            if (sessions == null || sessions.Count == 0)
            {
                return string.Empty;
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i] != null && sessions[i].IsActive)
                {
                    return sessions[i].SessionId ?? string.Empty;
                }
            }

            return sessions[0] == null ? string.Empty : (sessions[0].SessionId ?? string.Empty);
        }

        /// <summary>
        /// 构建成功响应 JSON。
        /// </summary>
        private string BuildSuccessResponse(string requestId, object payload)
        {
            return _serializer.Serialize(new Dictionary<string, object>
            {
                { "version", ProtocolVersion },
                { "requestId", requestId ?? string.Empty },
                { "success", true },
                { "payload", payload ?? new Dictionary<string, object>() },
                { "error", null }
            });
        }

        /// <summary>
        /// 构建失败响应 JSON。
        /// </summary>
        private string BuildErrorResponse(string requestId, string code, string message, string details)
        {
            return _serializer.Serialize(new Dictionary<string, object>
            {
                { "version", ProtocolVersion },
                { "requestId", requestId ?? string.Empty },
                { "success", false },
                { "payload", null },
                { "error", new Dictionary<string, object>
                    {
                        { "code", code ?? "error" },
                        { "message", message ?? "请求处理失败。" },
                        { "details", details ?? string.Empty }
                    }
                }
            });
        }

        /// <summary>
        /// JSON 反序列化为字典。
        /// </summary>
        private Dictionary<string, object> DeserializeObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            object obj = _serializer.DeserializeObject(json);
            var dictionary = obj as Dictionary<string, object>;
            return dictionary ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从字典读取字符串字段。
        /// </summary>
        private static string ReadString(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary == null || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            object value;
            if (!dictionary.TryGetValue(key, out value) || value == null)
            {
                return string.Empty;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        /// <summary>
        /// 从字典读取对象字段并转为字典。
        /// </summary>
        private static Dictionary<string, object> ReadDictionary(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary == null || string.IsNullOrWhiteSpace(key))
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            object value;
            if (!dictionary.TryGetValue(key, out value) || value == null)
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            var result = value as Dictionary<string, object>;
            return result ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
