using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SmartWord.AddIn.DI;
using SmartWord.Application.Orchestration;
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
                    Log.Information("收到前端请求：{RequestJson}", requestJson);

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

                    var orchestrator = ServiceLocator.GetRequiredService<IAgentOrchestrator>();
                    var intentRouter = ServiceLocator.GetRequiredService<IntentRouter>();
                    var contextHydrator = ServiceLocator.GetRequiredService<IContextHydrator>();
                    var llmClientOptions = ServiceLocator.GetRequiredService<LlmClientOptions>();
                    var smartWordSettings = ServiceLocator.GetCurrentSettingsSnapshot();

                    var manualMode = request.Value<string>("manualMode");
                    var selectedMode = await ResolveModeAsync(
                        manualMode,
                        content,
                        intentRouter,
                        contextHydrator,
                        requestCts.Token).ConfigureAwait(false);

                    Log.Information(
                        "请求模式识别完成：Mode={Mode}, IsAutoRouted={IsAutoRouted}",
                        selectedMode,
                        string.IsNullOrWhiteSpace(manualMode));

                    PostEventToJs(new
                    {
                        type = "mode_detected",
                        detectedMode = selectedMode.ToString().ToLowerInvariant(),
                        isAutoRouted = string.IsNullOrWhiteSpace(manualMode)
                    });

                    var customInstructions = request.Value<string>("customInstructions");
                    if (string.IsNullOrWhiteSpace(customInstructions))
                    {
                        customInstructions = smartWordSettings.CustomInstructions;
                    }

                    var modelRoute = llmClientOptions.ResolveModelRoute(selectedMode);
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
                        MaxIterations = request.Value<int?>("maxIterations") ?? 8,
                        RequireConfirmationForScripts =
                            request.Value<bool?>("requireConfirmationForScripts")
                            ?? smartWordSettings.RequireConfirmationForScripts,
                        EnableToolCalling = modelRoute.EnableToolCalling,
                        ModelRoutingMessage = modelRoute.RoutingMessage ?? string.Empty,
                        CustomSystemInstructions = customInstructions ?? string.Empty
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
                catch (OperationCanceledException)
                {
                    PostEventToJs(new
                    {
                        type = "cancelled",
                        message = "任务已取消。"
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

        public void PostEventToJs(object agentEvent)
        {
            if (_coreWebView2 == null)
            {
                return;
            }

            var eventJson = JsonConvert.SerializeObject(agentEvent);
            if (_ownerControl.InvokeRequired)
            {
                _ownerControl.BeginInvoke(new Action(() => _coreWebView2.PostWebMessageAsJson(eventJson)));
                return;
            }

            _coreWebView2.PostWebMessageAsJson(eventJson);
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

        private static async Task<AgentMode> ResolveModeAsync(
            string manualMode,
            string content,
            IntentRouter intentRouter,
            IContextHydrator contextHydrator,
            CancellationToken cancellationToken)
        {
            if (TryParseMode(manualMode, out var parsedManualMode))
            {
                return parsedManualMode;
            }

            var documentContext = await contextHydrator.HydrateAsync(cancellationToken).ConfigureAwait(false);
            return await intentRouter.RouteAsync(content, documentContext, cancellationToken).ConfigureAwait(false);
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
                isAutoRouted = agentEvent.IsAutoRouted,
                message = agentEvent.Message,
                citations = agentEvent.Citations
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
                case AgentEventType.ChangeApplied:
                    return "change_applied";
                case AgentEventType.ModeDetected:
                    return "mode_detected";
                case AgentEventType.DocumentMismatch:
                    return "document_mismatch";
                case AgentEventType.DocumentNotWritable:
                    return "document_not_writable";
                case AgentEventType.Cancelled:
                    return "cancelled";
                case AgentEventType.Error:
                default:
                    return "error";
            }
        }
    }
}
