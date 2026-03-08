using SmartWord.Core.Models.Conversation;
using SmartWord.Core.Orchestration.Conversation;
using SmartWord.Core.Abstractions;
using SmartWord.Services.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
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

            var request = new ChatTurnRequest
            {
                SessionId = ReadString(payload, "sessionId"),
                UserMessage = userMessage,
                ModelOverride = ReadString(payload, "modelOverride"),
                PromptVersion = ReadString(payload, "promptVersion"),
                ModeLock = ParseModeLock(ReadString(payload, "modeLock"))
            };

            ChatTurnResult result = await _conversationOrchestrator.RunTurnAsync(request).ConfigureAwait(false);
            Dictionary<string, object> sessionsPayload = await BuildSessionsPayloadAsync(result == null ? string.Empty : result.SessionId).ConfigureAwait(false);

            sessionsPayload["result"] = BuildTurnResult(result);
            return sessionsPayload;
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
            sessionsPayload["result"] = BuildApplyResult(result);
            return sessionsPayload;
        }

        /// <summary>
        /// 构建会话数据响应。
        /// </summary>
        private async Task<Dictionary<string, object>> BuildSessionsPayloadAsync(string preferredSessionId)
        {
            IReadOnlyList<ConversationSession> sessions = await _conversationOrchestrator.LoadSessionsAsync().ConfigureAwait(false);
            string activeSessionId = ResolveActiveSessionId(sessions, preferredSessionId);
            return new Dictionary<string, object>
            {
                { "sessions", BuildSessionDtos(sessions) },
                { "activeSessionId", activeSessionId }
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
                    { "messages", BuildMessageDtos(session == null ? null : session.Messages) }
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
        private static object BuildTurnResult(ChatTurnResult result)
        {
            if (result == null)
            {
                return new Dictionary<string, object>();
            }

            return new Dictionary<string, object>
            {
                { "sessionId", result.SessionId ?? string.Empty },
                { "assistantReply", result.AssistantReply ?? string.Empty },
                { "pendingActionId", result.PendingActionId ?? string.Empty },
                { "requiresUserConfirmation", result.RequiresUserConfirmation },
                { "resolvedMode", RouteToModeKey(result.ResolvedMode) }
            };
        }

        /// <summary>
        /// 构建动作应用结果 DTO。
        /// </summary>
        private static object BuildApplyResult(ApplyActionResult result)
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
                { "message", result.Message ?? string.Empty }
            };
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
