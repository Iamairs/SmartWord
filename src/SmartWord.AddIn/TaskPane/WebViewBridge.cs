using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SmartWord.AddIn.DI;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.Configuration;
using SmartWord.Infrastructure.LlmClients;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.AddIn.TaskPane
{
    /// <summary>
    /// 负责 WebView2 与 C# 之间的请求桥接。
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public class SmartWordBridge
    {
        private readonly Control _ownerControl;
        private readonly object _ctsSyncRoot = new object();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingToolConfirmations =
            new ConcurrentDictionary<string, TaskCompletionSource<bool>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _earlyConfirmationResults =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingQuestionAnswers =
            new ConcurrentDictionary<string, TaskCompletionSource<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _earlyQuestionAnswers =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<TodoBoardRecoveryDecision>> _pendingTodoRecoveryDecisions =
            new ConcurrentDictionary<string, TaskCompletionSource<TodoBoardRecoveryDecision>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TodoBoardRecoveryDecision> _earlyTodoRecoveryDecisions =
            new ConcurrentDictionary<string, TodoBoardRecoveryDecision>(StringComparer.OrdinalIgnoreCase);
        private CoreWebView2 _coreWebView2;
        private CancellationTokenSource _currentCts;

        public SmartWordBridge(Control ownerControl)
        {
            _ownerControl = ownerControl;
        }

        public void AttachToWebView(CoreWebView2 coreWebView2)
        {
            _coreWebView2 = coreWebView2;
        }

        public string GetSettingsJson()
        {
            var settings = ServiceLocator.GetCurrentSettingsSnapshot();
            return JsonConvert.SerializeObject(settings);
        }

        public string SaveSettingsJson(string settingsJson)
        {
            try
            {
                var incomingSettings = JsonConvert.DeserializeObject<SmartWordSettings>(settingsJson)
                    ?? new SmartWordSettings();
                var savedSettings = ServiceLocator.SaveSettings(incomingSettings);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "设置已保存。",
                    settings = savedSettings
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存 SmartWord 设置失败。");
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = ex.Message
                });
            }
        }

        public void SendMessageAsync(string requestJson)
        {
            Task.Run(async () =>
            {
                var requestCts = ReplaceCurrentCancellationTokenSource();
                try
                {
                    Log.Information("收到前端请求。RequestSummary={RequestSummary}", SummarizeRequestJson(requestJson));

                    var request = JObject.Parse(requestJson);
                    var content = request.Value<string>("content") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        PostEventToJs(new
                        {
                            type = "error",
                            message = "请输入消息内容。"
                        });

                        return;
                    }

                    var manualMode = request.Value<string>("manualMode");
                    if (string.IsNullOrWhiteSpace(manualMode))
                    {
                        Log.Warning("前端请求缺少 manualMode，已拒绝执行。");
                        PostEventToJs(new
                        {
                            type = "error",
                            message = "请求缺少运行模式，请先在前端选择“对话交流”“规划任务”或“自主执行”。"
                        });

                        return;
                    }

                    if (!TryParseMode(manualMode, out var selectedMode))
                    {
                        Log.Warning("前端请求包含无效 manualMode。ManualMode={ManualMode}", manualMode);
                        PostEventToJs(new
                        {
                            type = "error",
                            message = $"请求包含无效的运行模式：{manualMode}。"
                        });

                        return;
                    }

                    var orchestrator = ServiceLocator.GetRequiredService<IAgentOrchestrator>();
                    var llmClientOptions = ServiceLocator.GetRequiredService<LlmClientOptions>();
                    var smartWordSettings = ServiceLocator.GetCurrentSettingsSnapshot();

                    Log.Information(
                        "请求模式已确定：SelectedMode={SelectedMode}",
                        selectedMode);

                    PostEventToJs(new
                    {
                        type = "mode_detected",
                        detectedMode = selectedMode.ToString().ToLowerInvariant()
                    });

                    var customInstructions = request.Value<string>("customInstructions");
                    if (string.IsNullOrWhiteSpace(customInstructions))
                    {
                        customInstructions = smartWordSettings.CustomInstructions;
                    }

                    var modelRoute = llmClientOptions.ResolveModelRoute(selectedMode);
                    var hasStartupTodoBoardDecision = TryParseTodoRecoveryDecision(
                        request.Value<string>("todoBoardDecision"),
                        out var startupTodoBoardDecision);
                    Log.Information(
                        "模型能力分流完成：Mode={Mode}, SelectedModel={SelectedModel}, EnableToolCalling={EnableToolCalling}, UsedFallbackModel={UsedFallbackModel}, RequiresReasoningContentReplay={RequiresReasoningContentReplay}, CapabilitySource={CapabilitySource}, RoutingMessage={RoutingMessage}",
                        selectedMode,
                        modelRoute.SelectedModel,
                        modelRoute.EnableToolCalling,
                        modelRoute.UsedFallbackModel,
                        modelRoute.SelectedCapability == null ? false : modelRoute.SelectedCapability.RequiresReasoningContentReplay,
                        modelRoute.SelectedCapability == null ? string.Empty : modelRoute.SelectedCapability.CapabilitySource,
                        modelRoute.RoutingMessage);

                    var options = new AgentRunOptions
                    {
                        Mode = selectedMode,
                        Model = modelRoute.SelectedModel,
                        MaxIterations = request.Value<int?>("maxIterations") ?? 100,
                        RequireConfirmationForScripts =
                            request.Value<bool?>("requireConfirmationForScripts")
                            ?? smartWordSettings.RequireConfirmationForScripts,
                        EnableToolCalling = modelRoute.EnableToolCalling,
                        ModelRoutingMessage = modelRoute.RoutingMessage ?? string.Empty,
                        CustomSystemInstructions = customInstructions ?? string.Empty,
                        StartupTodoBoardDecision = hasStartupTodoBoardDecision
                            ? startupTodoBoardDecision
                            : (TodoBoardRecoveryDecision?)null,
                        ActivePlan = request["activePlan"] == null
                            ? null
                            : request["activePlan"].ToObject<ExecutionPlan>()
                    };

                    Log.Information(
                        "开始执行主编排流程：Mode={Mode}, Model={Model}",
                        options.Mode,
                        options.Model);

                    await foreach (var agentEvent in orchestrator.RunAsync(content, options, requestCts.Token))
                    {
                        PostEventToJs(MapEventToJs(agentEvent));
                    }
                }
                catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
                {
                    PostEventToJs(new
                    {
                        type = "cancelled",
                        message = "任务已取消。"
                    });
                }
                catch (OperationCanceledException ex)
                {
                    Log.Error(ex, "任务在执行过程中发生了非预期的取消异常。");
                    PostEventToJs(new
                    {
                        type = "error",
                        message = "任务在执行过程中被中断，请检查上一个失败步骤并重试。"
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "处理前端请求失败。");
                    PostEventToJs(new
                    {
                        type = "error",
                        message = ex.Message
                    });
                }
                finally
                {
                    DisposeCancellationTokenSource(requestCts);
                }
            });
        }

        public void NavigateToParagraph(int paragraphIndex)
        {
            Task.Run(async () =>
            {
                try
                {
                    var wordWrapper = ServiceLocator.GetRequiredService<WordApplicationWrapper>();
                    await wordWrapper.NavigateToParagraphAsync(paragraphIndex).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "段落跳转失败。ParagraphIndex={ParagraphIndex}", paragraphIndex);
                }
            });
        }

        public string ConfirmToolCall(string toolCallId, bool confirmed)
        {
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = "toolCallId 不能为空。"
                });
            }

            if (_pendingToolConfirmations.TryRemove(toolCallId, out var pendingConfirmation))
            {
                pendingConfirmation.TrySetResult(confirmed);
            }
            else
            {
                _earlyConfirmationResults[toolCallId] = confirmed;
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                toolCallId,
                confirmed
            });
        }

        public string SubmitTodoBoardRecoveryDecision(string recoveryRequestId, string decision)
        {
            if (string.IsNullOrWhiteSpace(recoveryRequestId))
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = "recoveryRequestId 不能为空。"
                });
            }

            if (!TryParseTodoRecoveryDecision(decision, out var parsedDecision))
            {
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    message = "恢复决策非法。允许值：recover_existing、rebuild_from_active_plan、discard_and_create_empty。"
                });
            }

            if (_pendingTodoRecoveryDecisions.TryRemove(recoveryRequestId, out var pendingDecision))
            {
                pendingDecision.TrySetResult(parsedDecision);
            }
            else
            {
                _earlyTodoRecoveryDecisions[recoveryRequestId] = parsedDecision;
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                recoveryRequestId,
                decision = decision ?? string.Empty
            });
        }

        public void PostEventToJs(object agentEvent)
        {
            if (_coreWebView2 == null || !IsOwnerControlAvailable())
            {
                return;
            }

            var eventJson = JsonConvert.SerializeObject(agentEvent);
            if (_ownerControl.InvokeRequired)
            {
                try
                {
                    _ownerControl.BeginInvoke(new Action(() =>
                    {
                        if (!IsOwnerControlAvailable() || _coreWebView2 == null)
                        {
                            return;
                        }

                        TryPostWebMessage(eventJson);
                    }));
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }

                return;
            }

            TryPostWebMessage(eventJson);
        }

        internal async Task<bool> WaitForToolConfirmationAsync(string toolCallId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(toolCallId))
            {
                throw new ArgumentException("toolCallId 不能为空。", nameof(toolCallId));
            }

            if (_earlyConfirmationResults.TryRemove(toolCallId, out var earlyResult))
            {
                return earlyResult;
            }

            var taskCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingToolConfirmations.TryAdd(toolCallId, taskCompletionSource))
            {
                if (_pendingToolConfirmations.TryGetValue(toolCallId, out var existing))
                {
                    return await existing.Task.ConfigureAwait(false);
                }

                return false;
            }

            var registration = cancellationToken.Register(() =>
            {
                if (_pendingToolConfirmations.TryRemove(toolCallId, out var pending))
                {
                    pending.TrySetCanceled(cancellationToken);
                }
            });

            try
            {
                return await taskCompletionSource.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
            }
        }

        /// <summary>前端调用此方法提交采访问题的答案。</summary>
        public string SubmitQuestionAnswer(string questionId, string answer)
        {
            if (string.IsNullOrWhiteSpace(questionId))
                return JsonConvert.SerializeObject(new { success = false, message = "questionId 不能为空。" });

            var safeAnswer = answer ?? string.Empty;
            if (_pendingQuestionAnswers.TryRemove(questionId, out var tcs))
            {
                tcs.TrySetResult(safeAnswer);
            }
            else
            {
                _earlyQuestionAnswers[questionId] = safeAnswer;
            }

            return JsonConvert.SerializeObject(new { success = true });
        }

        internal async Task<string> WaitForQuestionAnswerAsync(string questionId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(questionId))
                throw new ArgumentException("questionId 不能为空。", nameof(questionId));

            if (_earlyQuestionAnswers.TryRemove(questionId, out var earlyAnswer))
                return earlyAnswer;

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingQuestionAnswers.TryAdd(questionId, tcs))
            {
                if (_pendingQuestionAnswers.TryGetValue(questionId, out var existing))
                    return await existing.Task.ConfigureAwait(false);
                return string.Empty;
            }

            var registration = cancellationToken.Register(() =>
            {
                if (_pendingQuestionAnswers.TryRemove(questionId, out var pending))
                    pending.TrySetCanceled(cancellationToken);
            });

            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
            }
        }

        internal async Task<TodoBoardRecoveryDecision> WaitForTodoBoardRecoveryDecisionAsync(
            string recoveryRequestId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(recoveryRequestId))
            {
                throw new ArgumentException("recoveryRequestId 不能为空。", nameof(recoveryRequestId));
            }

            if (_earlyTodoRecoveryDecisions.TryRemove(recoveryRequestId, out var earlyDecision))
            {
                return earlyDecision;
            }

            var taskCompletionSource =
                new TaskCompletionSource<TodoBoardRecoveryDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingTodoRecoveryDecisions.TryAdd(recoveryRequestId, taskCompletionSource))
            {
                if (_pendingTodoRecoveryDecisions.TryGetValue(recoveryRequestId, out var existing))
                {
                    return await existing.Task.ConfigureAwait(false);
                }

                return TodoBoardRecoveryDecision.RecoverExisting;
            }

            var registration = cancellationToken.Register(() =>
            {
                if (_pendingTodoRecoveryDecisions.TryRemove(recoveryRequestId, out var pending))
                {
                    pending.TrySetCanceled(cancellationToken);
                }
            });

            try
            {
                return await taskCompletionSource.Task.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
            }
        }

        private CancellationTokenSource ReplaceCurrentCancellationTokenSource()
        {
            lock (_ctsSyncRoot)
            {
                _currentCts?.Cancel();
                _currentCts?.Dispose();
                _currentCts = new CancellationTokenSource();
                return _currentCts;
            }
        }

        private void DisposeCancellationTokenSource(CancellationTokenSource cancellationTokenSource)
        {
            lock (_ctsSyncRoot)
            {
                if (ReferenceEquals(_currentCts, cancellationTokenSource))
                {
                    _currentCts = null;
                }
            }

            cancellationTokenSource.Dispose();
        }

        private static bool TryParseMode(string manualMode, out AgentMode mode)
        {
            mode = AgentMode.Agent;
            if (string.IsNullOrWhiteSpace(manualMode))
            {
                return false;
            }

            switch (manualMode.Trim().ToLowerInvariant())
            {
                case "ask":
                    mode = AgentMode.Ask;
                    return true;
                case "plan":
                    mode = AgentMode.Plan;
                    return true;
                case "agent":
                    mode = AgentMode.Agent;
                    return true;
                default:
                    return false;
            }
        }

        private bool IsOwnerControlAvailable()
        {
            return _ownerControl != null
                && !_ownerControl.IsDisposed
                && _ownerControl.IsHandleCreated;
        }

        private void TryPostWebMessage(string eventJson)
        {
            try
            {
                _coreWebView2?.PostWebMessageAsJson(eventJson);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (COMException)
            {
            }
        }

        private static object MapEventToJs(AgentEvent agentEvent)
        {
            return new
            {
                type = ToJsEventType(agentEvent.Type),
                content = agentEvent.Content,
                toolName = agentEvent.ToolName,
                toolInput = agentEvent.ToolInput,
                toolOutput = agentEvent.ToolOutput,
                toolSuccess = agentEvent.ToolSuccess,
                requiresConfirmation = agentEvent.RequiresConfirmation,
                toolCallId = agentEvent.ToolCallId,
                paragraphRefs = agentEvent.ParagraphRefs,
                affectedParagraphs = agentEvent.AffectedParagraphs,
                operationDescription = agentEvent.OperationDescription,
                completedSteps = agentEvent.CompletedSteps,
                totalSteps = agentEvent.TotalSteps,
                detectedMode = agentEvent.DetectedMode,
                message = agentEvent.Message,
                citations = agentEvent.Citations,
                questionOptions = agentEvent.QuestionOptions,
                planJson = agentEvent.PlanJson,
                boardJson = agentEvent.BoardJson,
                currentTodoId = agentEvent.CurrentTodoId,
                recoveryRequestId = agentEvent.RecoveryRequestId,
                recoveryReason = agentEvent.RecoveryReason,
                lastRunOutcome = agentEvent.LastRunOutcome,
                lastErrorSummary = agentEvent.LastErrorSummary,
                hasActivePlan = agentEvent.HasActivePlan,
                canRecoverExisting = agentEvent.CanRecoverExisting,
                todoBoardUpdateKind = agentEvent.TodoBoardUpdateKind
            };
        }

        private static string ToJsEventType(AgentEventType type)
        {
            switch (type)
            {
                case AgentEventType.StreamChunk:
                    return "stream_chunk";
                case AgentEventType.ToolCallStarted:
                    return "tool_call_started";
                case AgentEventType.ToolCallCompleted:
                    return "tool_call_completed";
                case AgentEventType.ToolCallDenied:
                    return "tool_call_denied";
                case AgentEventType.ToolCallSkipped:
                    return "tool_call_skipped";
                case AgentEventType.ContextCompacted:
                    return "context_compacted";
                case AgentEventType.TaskCompleted:
                    return "task_completed";
                case AgentEventType.MaxIterationsReached:
                    return "max_iterations_reached";
                case AgentEventType.ProgressUpdate:
                    return "progress_update";
                case AgentEventType.ChangeExecuted:
                    return "change_executed";
                case AgentEventType.ChangeApplied:
                    return "change_applied";
                case AgentEventType.ChangeUnverified:
                    return "change_unverified";
                case AgentEventType.ChangeVerificationFailed:
                    return "change_verification_failed";
                case AgentEventType.ChangeRepairRequired:
                    return "change_repair_required";
                case AgentEventType.ModeDetected:
                    return "mode_detected";
                case AgentEventType.DocumentMismatch:
                    return "document_mismatch";
                case AgentEventType.DocumentNotWritable:
                    return "document_not_writable";
                case AgentEventType.Cancelled:
                    return "cancelled";
                case AgentEventType.QuestionAsked:
                    return "question_asked";
                case AgentEventType.PlanReady:
                    return "plan_ready";
                case AgentEventType.TodoBoardReady:
                    return "todo_board_ready";
                case AgentEventType.TodoBoardUpdated:
                    return "todo_board_updated";
                case AgentEventType.TodoReminderInjected:
                    return "todo_reminder_injected";
                case AgentEventType.TodoBoardRecoveryRequired:
                    return "todo_board_recovery_required";
                case AgentEventType.TodoBoardPaused:
                    return "todo_board_paused";
                case AgentEventType.Error:
                default:
                    return "error";
            }
        }

        private static bool TryParseTodoRecoveryDecision(string rawDecision, out TodoBoardRecoveryDecision decision)
        {
            decision = TodoBoardRecoveryDecision.RecoverExisting;
            if (string.IsNullOrWhiteSpace(rawDecision))
            {
                return false;
            }

            switch (rawDecision.Trim().ToLowerInvariant())
            {
                case "recover_existing":
                    decision = TodoBoardRecoveryDecision.RecoverExisting;
                    return true;
                case "rebuild_from_active_plan":
                    decision = TodoBoardRecoveryDecision.RebuildFromActivePlan;
                    return true;
                case "discard_and_create_empty":
                    decision = TodoBoardRecoveryDecision.DiscardAndCreateEmpty;
                    return true;
                default:
                    return false;
            }
        }

        private static string SummarizeRequestJson(string requestJson)
        {
            if (string.IsNullOrWhiteSpace(requestJson))
            {
                return "jsonLen=0";
            }

            try
            {
                var request = JObject.Parse(requestJson);
                var content = request.Value<string>("content") ?? string.Empty;
                var manualMode = request.Value<string>("manualMode") ?? string.Empty;
                var customInstructions = request.Value<string>("customInstructions") ?? string.Empty;

                return "jsonLen="
                    + requestJson.Length
                    + ", mode="
                    + (string.IsNullOrWhiteSpace(manualMode) ? "empty" : manualMode)
                    + ", contentLen="
                    + content.Length
                    + ", hasCustomInstructions="
                    + (!string.IsNullOrWhiteSpace(customInstructions))
                    + ", requireConfirmationForScripts="
                    + (request.Value<bool?>("requireConfirmationForScripts") ?? true)
                    + ", maxIterations="
                    + (request.Value<int?>("maxIterations") ?? 100);
            }
            catch (Exception ex)
            {
                return "jsonLen=" + requestJson.Length + ", parseFailed=" + ex.GetType().Name;
            }
        }
    }
}
