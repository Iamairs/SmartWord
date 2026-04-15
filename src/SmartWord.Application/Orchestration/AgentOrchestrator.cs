using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SmartWord.Application.Context;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Tools;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// Ask/Plan/Agent 共用的主编排循环，Phase 2 先完整支持 Ask 模式只读工具链路。
    /// </summary>
    public sealed class AgentOrchestrator : IAgentOrchestrator
    {
        private const int AskModeMaxIterations = 5;
        private const int MaxToolCallsPerIteration = 5;
        private const int ConsecutiveFailureThreshold = 3;
        private static readonly TimeSpan ToolExecutionTimeout = TimeSpan.FromSeconds(30);
        private const int ToolErrorMessageMaxLength = 500;
        private const int CompactionThreshold = 80000;

        private readonly ILlmClient _llmClient;
        private readonly IContextHydrator _contextHydrator;
        private readonly IConversationStore _conversationStore;
        private readonly SystemPromptBuilder _systemPromptBuilder;
        private readonly IToolRegistry _toolRegistry;
        private readonly PermissionGuard _permissionGuard;
        private readonly IConfirmationChannel _confirmationChannel;
        private readonly IUndoScopeFactory _undoScopeFactory;
        private readonly ConversationCompressor _conversationCompressor;

        public AgentOrchestrator(
            ILlmClient llmClient,
            IContextHydrator contextHydrator,
            IConversationStore conversationStore,
            SystemPromptBuilder systemPromptBuilder,
            IToolRegistry toolRegistry,
            PermissionGuard permissionGuard,
            IConfirmationChannel confirmationChannel,
            IUndoScopeFactory undoScopeFactory,
            ConversationCompressor conversationCompressor)
        {
            _llmClient = llmClient;
            _contextHydrator = contextHydrator;
            _conversationStore = conversationStore;
            _systemPromptBuilder = systemPromptBuilder;
            _toolRegistry = toolRegistry;
            _permissionGuard = permissionGuard;
            _confirmationChannel = confirmationChannel;
            _undoScopeFactory = undoScopeFactory;
            _conversationCompressor = conversationCompressor ?? throw new ArgumentNullException(nameof(conversationCompressor));
        }

        /// <summary>
        /// 运行一次 Agent 编排流程：
        /// 1) 读取文档上下文并拼装消息
        /// 2) 调用 LLM（可选工具调用）
        /// 3) 执行工具并回填结果
        /// 4) 输出流式事件与最终完成事件
        /// </summary>
        public async IAsyncEnumerable<AgentEvent> RunAsync(
    string userInput,
    AgentRunOptions options,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
{
    var safeOptions = options ?? new AgentRunOptions();
    var documentContext = await _contextHydrator.HydrateAsync(cancellationToken).ConfigureAwait(false);
    var documentPath = string.IsNullOrWhiteSpace(documentContext.DocumentPath)
        ? "__active_document__"
        : documentContext.DocumentPath;

    if (safeOptions.Mode == AgentMode.Agent
        && (documentContext.DocumentStatus == null || !documentContext.DocumentStatus.IsWritable))
    {
        yield return new AgentEvent
        {
            Type = AgentEventType.DocumentNotWritable,
            Message = documentContext.DocumentStatus == null
                ? "文档当前不可写，系统已停止执行。"
                : documentContext.DocumentStatus.GetUserFriendlyMessage()
        };

        yield break;
    }

    var userMessage = new AgentMessage
    {
        Role = "user",
        Content = userInput ?? string.Empty
    };

    await _conversationStore
        .AppendUserMessageAsync(documentPath, userMessage, cancellationToken)
        .ConfigureAwait(false);

    var history = await _conversationStore
        .GetHistoryAsync(documentPath, cancellationToken)
        .ConfigureAwait(false);

    var messages = new List<AgentMessage>();
    var systemPrompt = BuildSystemPrompt(safeOptions, documentContext);
    if (!string.IsNullOrWhiteSpace(systemPrompt))
    {
        messages.Add(new AgentMessage
        {
            Role = "system",
            Content = systemPrompt
        });
    }

    messages.AddRange(history);

    if (!string.IsNullOrWhiteSpace(safeOptions.ModelRoutingMessage))
    {
        Log.Information(
            "本次运行的模型能力分流说明：Mode={Mode}, Model={Model}, EnableToolCalling={EnableToolCalling}, RoutingMessage={RoutingMessage}",
            safeOptions.Mode,
            safeOptions.Model,
            safeOptions.EnableToolCalling,
            safeOptions.ModelRoutingMessage);
    }

    var toolDefinitions = safeOptions.EnableToolCalling
        ? _toolRegistry.GetToolDefinitions(safeOptions.Mode)
        : new List<ToolDefinition>();
    var citationRegistry = new Dictionary<int, CitationEntry>();
    var paragraphToRef = new Dictionary<int, int>();
    var nextCitationRef = 1;
    var maxIterations = ResolveMaxIterations(safeOptions);
    var consecutiveFailures = 0;
    var shouldCommitUndo = false;
    var hasCompactedContext = false;
    AgentMessage finalAssistantMessage = null;
    IUndoScope undoScope = null;

    try
    {
        if (safeOptions.Mode == AgentMode.Agent && _undoScopeFactory != null)
        {
            undoScope = await _undoScopeFactory
                .BeginTaskUndoAsync("SmartWord Agent 写入任务", cancellationToken)
                .ConfigureAwait(false);
        }

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var latestContext = await _contextHydrator.HydrateAsync(cancellationToken).ConfigureAwait(false);
            var latestDocumentPath = string.IsNullOrWhiteSpace(latestContext.DocumentPath)
                ? "__active_document__"
                : latestContext.DocumentPath;
            if (!string.Equals(latestDocumentPath, documentPath, StringComparison.OrdinalIgnoreCase))
            {
                yield return new AgentEvent
                {
                    Type = AgentEventType.DocumentMismatch,
                    Message = "检测到活动文档已切换，任务已停止。当前回滚仅能做最佳努力处理，请确认文档内容。"
                };

                yield break;
            }

            var compactionThreshold = safeOptions.CompactionThreshold > 0
                ? safeOptions.CompactionThreshold
                : CompactionThreshold;
            var estimatedTokenCount = _conversationStore.EstimateTokenCount(messages);
            if (estimatedTokenCount > compactionThreshold)
            {
                var compactedMessages = _conversationCompressor.Compress(messages);
                var compactedTokenCount = _conversationStore.EstimateTokenCount(compactedMessages);
                var canContinueWithCompactedContext = compactedMessages != null
                    && compactedMessages.Count < messages.Count
                    && compactedTokenCount < estimatedTokenCount;

                yield return new AgentEvent
                {
                    Type = AgentEventType.ContextCompacted,
                    Message = canContinueWithCompactedContext
                        ? "当前对话已接近上下文上限，系统已压缩较早消息并继续执行。"
                        : "当前对话已接近上下文上限，压缩后仍不足以继续执行，系统已停止本轮任务。"
                };

                if (!canContinueWithCompactedContext || hasCompactedContext)
                {
                    break;
                }

                messages = compactedMessages.ToList();
                hasCompactedContext = true;
                continue;
            }

            AgentMessage assistantMessage;
            if (toolDefinitions.Count > 0)
            {
                var chunks = new ConcurrentQueue<string>();
                using (var signal = new SemaphoreSlim(0))
                {
                    var assistantTask = _llmClient.ChatCompletionWithToolsAsync(
                        messages,
                        safeOptions.Model,
                        toolDefinitions,
                        chunk =>
                        {
                            chunks.Enqueue(chunk);
                            signal.Release();
                        },
                        cancellationToken);

                    while (!assistantTask.IsCompleted || !chunks.IsEmpty)
                    {
                        while (chunks.TryDequeue(out var chunk))
                        {
                            yield return new AgentEvent
                            {
                                Type = AgentEventType.StreamChunk,
                                Content = chunk
                            };
                        }

                        if (assistantTask.IsCompleted)
                        {
                            break;
                        }

                        var waitTask = signal.WaitAsync(cancellationToken);
                        var completedTask = await Task.WhenAny(assistantTask, waitTask).ConfigureAwait(false);
                        if (completedTask == waitTask)
                        {
                            await waitTask.ConfigureAwait(false);
                        }
                    }

                    while (chunks.TryDequeue(out var remainingChunk))
                    {
                        yield return new AgentEvent
                        {
                            Type = AgentEventType.StreamChunk,
                            Content = remainingChunk
                        };
                    }

                    assistantMessage = await assistantTask.ConfigureAwait(false);
                }
            }
            else
            {
                var builder = new StringBuilder();
                await foreach (var chunk in _llmClient.ChatCompletionStreamAsync(messages, safeOptions.Model, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                    {
                        continue;
                    }

                    builder.Append(chunk);
                    yield return new AgentEvent
                    {
                        Type = AgentEventType.StreamChunk,
                        Content = chunk
                    };
                }

                assistantMessage = new AgentMessage
                {
                    Role = "assistant",
                    Content = builder.ToString()
                };
            }

            finalAssistantMessage = assistantMessage;
            await _conversationStore
                .AppendAssistantMessageAsync(documentPath, assistantMessage, cancellationToken)
                .ConfigureAwait(false);
            messages.Add(CloneMessage(assistantMessage));

            if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
            {
                break;
            }

            var toolCalls = assistantMessage.ToolCalls;
            if (toolCalls.Count > MaxToolCallsPerIteration)
            {
                toolCalls = toolCalls.Take(MaxToolCallsPerIteration).ToList();
                Log.Warning(
                    "本轮工具调用数量超过限制，已截断。MaxToolCallsPerIteration={MaxToolCallsPerIteration}",
                    MaxToolCallsPerIteration);
            }

            foreach (var toolCall in toolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tool = _toolRegistry.GetTool(toolCall.Name);
                var isWriteTool = IsWriteTool(tool);

                JObject parsedInput = null;
                ToolCallResult inputParseError = null;
                try
                {
                    parsedInput = string.IsNullOrWhiteSpace(toolCall.Input)
                        ? new JObject()
                        : JObject.Parse(toolCall.Input);
                }
                catch (Exception ex)
                {
                    inputParseError = ToolCallResult.Error(toolCall.Name, Truncate(ex.Message, ToolErrorMessageMaxLength));
                }

                var operationDescription = BuildOperationDescription(toolCall.Name, parsedInput);
                var requiresConfirmation = safeOptions.Mode == AgentMode.Agent
                    && safeOptions.RequireConfirmationForScripts
                    && isWriteTool;

                yield return new AgentEvent
                {
                    Type = AgentEventType.ToolCallStarted,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.Name,
                    ToolInput = toolCall.Input ?? string.Empty,
                    RequiresConfirmation = requiresConfirmation,
                    OperationDescription = operationDescription
                };

                if (!_permissionGuard.IsAllowed(toolCall.Name, safeOptions.Mode))
                {
                    var deniedResult = ToolCallResult.Denied(toolCall.Name);
                    await AppendToolResultAsync(documentPath, messages, toolCall, deniedResult, cancellationToken)
                        .ConfigureAwait(false);

                    yield return new AgentEvent
                    {
                        Type = AgentEventType.ToolCallDenied,
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        ToolInput = toolCall.Input ?? string.Empty,
                        ToolOutput = deniedResult.Output,
                        ToolSuccess = deniedResult.Success,
                        OperationDescription = operationDescription
                    };

                    consecutiveFailures++;
                    if (consecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        yield return CreateCircuitBreakerEvent();
                        yield break;
                    }

                    continue;
                }

                if (inputParseError != null)
                {
                    await AppendToolResultAsync(documentPath, messages, toolCall, inputParseError, cancellationToken)
                        .ConfigureAwait(false);

                    yield return CreateToolCompletedEvent(toolCall, inputParseError);
                    consecutiveFailures++;
                    if (consecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        yield return CreateCircuitBreakerEvent();
                        yield break;
                    }

                    continue;
                }

                if (requiresConfirmation)
                {
                    if (_confirmationChannel == null || !_confirmationChannel.IsAvailable)
                    {
                        var unavailableResult = ToolCallResult.Denied(
                            toolCall.Name,
                            "当前未连接确认通道，系统已拒绝执行写操作。");
                        await AppendToolResultAsync(documentPath, messages, toolCall, unavailableResult, cancellationToken)
                            .ConfigureAwait(false);

                        yield return new AgentEvent
                        {
                            Type = AgentEventType.ToolCallDenied,
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            ToolInput = toolCall.Input ?? string.Empty,
                            ToolOutput = unavailableResult.Output,
                            ToolSuccess = unavailableResult.Success,
                            OperationDescription = operationDescription
                        };

                        consecutiveFailures++;
                        if (consecutiveFailures >= ConsecutiveFailureThreshold)
                        {
                            yield return CreateCircuitBreakerEvent();
                            yield break;
                        }

                        continue;
                    }

                    var confirmed = await _confirmationChannel
                        .WaitForConfirmationAsync(toolCall.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (!confirmed)
                    {
                        var skippedResult = ToolCallResult.Skipped(toolCall.Name, "用户选择跳过本次写操作。");
                        await AppendToolResultAsync(documentPath, messages, toolCall, skippedResult, cancellationToken)
                            .ConfigureAwait(false);

                        yield return new AgentEvent
                        {
                            Type = AgentEventType.ToolCallSkipped,
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            ToolInput = toolCall.Input ?? string.Empty,
                            ToolOutput = skippedResult.Output,
                            ToolSuccess = skippedResult.Success,
                            OperationDescription = operationDescription
                        };

                        consecutiveFailures = 0;
                        continue;
                    }
                }

                ToolCallResult executionResult;
                try
                {
                    if (tool == null)
                    {
                        executionResult = ToolCallResult.Error(toolCall.Name, "未找到对应的工具实现。");
                    }
                    else
                    {
                        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            timeoutCts.CancelAfter(ToolExecutionTimeout);
                            using (var inputDocument = JsonDocument.Parse(parsedInput.ToString(Formatting.None)))
                            {
                                var toolTask = tool.ExecuteAsync(
                                    inputDocument.RootElement.Clone(),
                                    undoScope,
                                    timeoutCts.Token);
                                var completedTask = await Task.WhenAny(
                                        toolTask,
                                        Task.Delay(ToolExecutionTimeout, cancellationToken))
                                    .ConfigureAwait(false);
                                executionResult = completedTask == toolTask
                                    ? await toolTask.ConfigureAwait(false)
                                    : ToolCallResult.Error(toolCall.Name, "工具执行超时。");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    executionResult = ToolCallResult.Error(toolCall.Name, "工具执行超时。");
                }
                catch (Exception ex)
                {
                    executionResult = ToolCallResult.Error(
                        toolCall.Name,
                        Truncate(ex.ToString(), ToolErrorMessageMaxLength));
                }

                if (executionResult.Success)
                {
                    executionResult.Output = DecorateToolOutput(
                        toolCall.Name,
                        executionResult.Output,
                        documentPath,
                        citationRegistry,
                        paragraphToRef,
                        ref nextCitationRef);
                }

                if (string.IsNullOrWhiteSpace(executionResult.OperationDescription))
                {
                    executionResult.OperationDescription = operationDescription;
                }

                await AppendToolResultAsync(documentPath, messages, toolCall, executionResult, cancellationToken)
                    .ConfigureAwait(false);

                yield return CreateToolCompletedEvent(toolCall, executionResult);

                if (executionResult.Success)
                {
                    consecutiveFailures = 0;
                    if (isWriteTool)
                    {
                        yield return new AgentEvent
                        {
                            Type = AgentEventType.ChangeApplied,
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            AffectedParagraphs = executionResult.AffectedParagraphs,
                            OperationDescription = executionResult.OperationDescription
                        };
                    }
                }
                else
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= ConsecutiveFailureThreshold)
                    {
                        yield return CreateCircuitBreakerEvent();
                        yield break;
                    }
                }
            }
        }

        shouldCommitUndo = true;
    }
    finally
    {
        if (undoScope != null)
        {
            try
            {
                if (shouldCommitUndo)
                {
                    undoScope.Commit();
                }
                else
                {
                    undoScope.Rollback();
                }
            }
            finally
            {
                undoScope.Dispose();
            }
        }
    }

    yield return new AgentEvent
    {
        Type = AgentEventType.TaskCompleted,
        Citations = BuildCitations(finalAssistantMessage?.Content, citationRegistry),
        Message = string.Empty
    };
}

        private async Task AppendToolResultAsync(
            string documentPath,
            IList<AgentMessage> messages,
            ToolCall toolCall,
            ToolCallResult result,
            CancellationToken cancellationToken)
        {
            await _conversationStore
                .AppendToolResultAsync(
                    documentPath,
                    toolCall.Id,
                    toolCall.Name,
                    toolCall.Input ?? string.Empty,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);

            messages.Add(new AgentMessage
            {
                Role = "tool",
                ToolCallId = toolCall.Id,
                Name = toolCall.Name,
                Content = result.Output ?? string.Empty,
                ToolName = toolCall.Name,
                RawToolInput = toolCall.Input ?? string.Empty,
                ToolSuccess = result.Success
            });
        }

        private string BuildSystemPrompt(AgentRunOptions options, DocumentContext documentContext)
        {
            var prompt = _systemPromptBuilder.Build(options.Mode);
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("--- DOCUMENT CONTEXT ---");
            contextBuilder.AppendLine(
                $"Document: {documentContext.DocumentName} ({documentContext.Complexity}: {documentContext.WordCount} words, {documentContext.ParagraphCount} paragraphs)");
            contextBuilder.AppendLine($"Path: {documentContext.DocumentPath}");
            contextBuilder.AppendLine($"Pages: {documentContext.CurrentPageNumber} / {documentContext.TotalPages}");
            contextBuilder.AppendLine($"Cursor: Paragraph #{documentContext.CursorParagraphIndex}");
            if (documentContext.HasSelection)
            {
                contextBuilder.AppendLine($"Selected: \"{documentContext.SelectedText}\" (Paragraph #{documentContext.SelectionParagraphIndex})");
            }

            contextBuilder.AppendLine(
                $"Stats: tables={documentContext.TableCount}, images={documentContext.ImageCount}, annotations={documentContext.AnnotationCount}");
            contextBuilder.AppendLine(
                $"Status: {(documentContext.DocumentStatus == null ? string.Empty : documentContext.DocumentStatus.GetUserFriendlyMessage())}");
            if (documentContext.DocumentStatus != null && documentContext.DocumentStatus.IsTrackChangesEnforced)
            {
                contextBuilder.AppendLine("Notice: 当前文档已启用修订模式，写入会以修订痕迹呈现，不应把它误判为失败。");
            }
            if (documentContext.Headings != null && documentContext.Headings.Count > 0)
            {
                contextBuilder.AppendLine("Document Outline:");
                foreach (var heading in documentContext.Headings)
                {
                    contextBuilder.AppendLine(
                        $"{new string(' ', Math.Max(0, (heading.Level - 1) * 2))}- {heading.Text} (¶{heading.ParagraphIndex})");
                }
            }

            var finalPrompt = string.IsNullOrWhiteSpace(prompt)
                ? contextBuilder.ToString()
                : prompt + Environment.NewLine + Environment.NewLine + contextBuilder;

            if (!string.IsNullOrWhiteSpace(options.CustomSystemInstructions))
            {
                finalPrompt += Environment.NewLine
                    + Environment.NewLine
                    + "--- USER CUSTOM INSTRUCTIONS ---"
                    + Environment.NewLine
                    + options.CustomSystemInstructions;
            }

            if (!options.EnableToolCalling)
            {
                finalPrompt += Environment.NewLine
                    + Environment.NewLine
                    + "--- MODEL CAPABILITY NOTICE ---"
                    + Environment.NewLine
                    + "当前模型不支持工具调用，你无法读取或检索 Word 文档内容。"
                    + Environment.NewLine
                    + "你必须明确说明这一限制，不能假装已经访问过文档。";
            }

            return finalPrompt;
        }

        private static AgentEvent CreateToolCompletedEvent(ToolCall toolCall, ToolCallResult result)
{
    return new AgentEvent
    {
        Type = AgentEventType.ToolCallCompleted,
        ToolCallId = toolCall.Id,
        ToolName = toolCall.Name,
        ToolInput = toolCall.Input ?? string.Empty,
        ToolOutput = result.Output ?? string.Empty,
        ToolSuccess = result.Success,
        ParagraphRefs = result.ParagraphRefs,
        AffectedParagraphs = result.AffectedParagraphs,
        OperationDescription = result.OperationDescription ?? string.Empty
    };
}

        private static string DecorateToolOutput(
            string toolName,
            string output,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var trimmedOutput = output.TrimStart();
            if (!trimmedOutput.StartsWith("{", StringComparison.Ordinal)
                && !trimmedOutput.StartsWith("[", StringComparison.Ordinal))
            {
                return output;
            }

            try
            {
                var token = JToken.Parse(output);
                var rootObject = token as JObject;
                var discovered = new JArray();

                switch (toolName ?? string.Empty)
                {
                    case "read_section":
                        AttachRefsOnArray(
                            token["paragraphs"] as JArray,
                            "index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                    case "grep_document":
                        AttachRefsOnArray(
                            token["results"] as JArray,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        foreach (var item in (token["results"] as JArray ?? new JArray()).OfType<JObject>())
                        {
                            AttachRefsOnArray(
                                item["context_before"] as JArray,
                                "index",
                                "text",
                                documentPath,
                                citationRegistry,
                                paragraphToRef,
                                ref nextCitationRef,
                                discovered);
                            AttachRefsOnArray(
                                item["context_after"] as JArray,
                                "index",
                                "text",
                                documentPath,
                                citationRegistry,
                                paragraphToRef,
                                ref nextCitationRef,
                                discovered);
                        }
                        break;
                    case "probe_document":
                        AttachRefsOnArray(
                            token["outline"] as JArray,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        AttachRefOnObject(
                            token["selection"] as JObject,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                    case "get_selection_context":
                        AttachRefOnObject(
                            token["selection"] as JObject,
                            "para_index",
                            "text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        AttachRefOnObject(
                            token["context"] as JObject,
                            "paragraph_index",
                            "paragraph_full",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                    case "read_annotations":
                        AttachRefsOnArray(
                            token["results"] as JArray,
                            "para_index",
                            "anchor_text",
                            documentPath,
                            citationRegistry,
                            paragraphToRef,
                            ref nextCitationRef,
                            discovered);
                        break;
                }

                if (discovered.Count > 0 && rootObject != null)
                {
                    rootObject["citation_entries"] = discovered;
                }

                return (rootObject ?? token).ToString(Formatting.None);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "装饰工具输出的引用映射失败。ToolName={ToolName}", toolName);
                return output;
            }
        }

        private static void AttachRefsOnArray(
            JArray array,
            string paragraphPropertyName,
            string excerptPropertyName,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef,
            JArray discovered)
        {
            if (array == null)
            {
                return;
            }

            foreach (var item in array.OfType<JObject>())
            {
                AttachRefOnObject(
                    item,
                    paragraphPropertyName,
                    excerptPropertyName,
                    documentPath,
                    citationRegistry,
                    paragraphToRef,
                    ref nextCitationRef,
                    discovered);
            }
        }

        private static void AttachRefOnObject(
            JObject target,
            string paragraphPropertyName,
            string excerptPropertyName,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef,
            JArray discovered)
        {
            if (target == null)
            {
                return;
            }

            var paragraphIndex = target.Value<int?>(paragraphPropertyName);
            if (!paragraphIndex.HasValue || paragraphIndex.Value < 0)
            {
                return;
            }

            var excerpt = target.Value<string>(excerptPropertyName) ?? string.Empty;
            var refId = RegisterCitation(
                paragraphIndex.Value,
                excerpt,
                documentPath,
                citationRegistry,
                paragraphToRef,
                ref nextCitationRef);
            target["ref"] = refId;
            discovered.Add(new JObject
            {
                ["ref"] = refId,
                ["paragraphIndex"] = paragraphIndex.Value,
                ["excerpt"] = excerpt
            });
        }

        private static int RegisterCitation(
            int paragraphIndex,
            string excerpt,
            string documentPath,
            IDictionary<int, CitationEntry> citationRegistry,
            IDictionary<int, int> paragraphToRef,
            ref int nextCitationRef)
        {
            if (paragraphToRef.TryGetValue(paragraphIndex, out var existingRef))
            {
                return existingRef;
            }

            var refId = nextCitationRef++;
            paragraphToRef[paragraphIndex] = refId;
            citationRegistry[refId] = new CitationEntry
            {
                Ref = refId,
                ParagraphIndex = paragraphIndex,
                Excerpt = excerpt,
                DocumentPath = documentPath
            };

            return refId;
        }

        private static List<CitationEntry> BuildCitations(
            string assistantContent,
            IReadOnlyDictionary<int, CitationEntry> citationRegistry)
        {
            var citations = new List<CitationEntry>();
            if (string.IsNullOrWhiteSpace(assistantContent) || citationRegistry == null || citationRegistry.Count == 0)
            {
                return citations;
            }

            foreach (Match match in Regex.Matches(assistantContent, @"\[(\d+)\]"))
            {
                if (!int.TryParse(match.Groups[1].Value, out var refId))
                {
                    continue;
                }

                if (!citationRegistry.TryGetValue(refId, out var citation))
                {
                    continue;
                }

                if (citations.Any(item => item.Ref == refId))
                {
                    continue;
                }

                citations.Add(citation);
            }

            return citations;
        }

        private static AgentEvent CreateCircuitBreakerEvent()
        {
            return new AgentEvent
            {
                Type = AgentEventType.Error,
                Message = "工具已连续失败 3 次，系统为防止误操作已停止本次任务。"
            };
        }

        private static bool IsWriteTool(ITool tool)
        {
            return tool != null && tool.RequiredPermission != ToolPermission.ReadOnly;
        }

        private static string BuildOperationDescription(string toolName, JObject parsedInput)
        {
            if (parsedInput != null)
            {
                var description = parsedInput.Value<string>("description");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }

                var operation = parsedInput.Value<string>("operation");
                if (!string.IsNullOrWhiteSpace(operation))
                {
                    switch ((toolName ?? string.Empty).Trim().ToLowerInvariant())
                    {
                        case "patch_range":
                            return "准备执行范围写入：" + operation.Trim();
                        case "verify_change":
                            return "准备验证改动结果：" + operation.Trim();
                        case "execute_script":
                            return "准备执行脚本写入：" + operation.Trim();
                    }
                }

                if (parsedInput.TryGetValue("operations", out var operationsToken)
                    && operationsToken is JArray operationsArray)
                {
                    return "准备执行范围写入，共 " + operationsArray.Count + " 个操作。";
                }
            }

            switch ((toolName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "patch_range":
                    return "准备执行文档范围写入。";
                case "verify_change":
                    return "准备验证本次改动结果。";
                case "execute_script":
                    return "准备执行脚本写入。";
                default:
                    return "准备执行工具：" + (toolName ?? string.Empty);
            }
        }

        private static AgentMessage CloneMessage(AgentMessage message)
        {
            return new AgentMessage
            {
                Role = message.Role,
                Content = message.Content,
                ReasoningContent = message.ReasoningContent,
                ToolCallId = message.ToolCallId,
                Name = message.Name,
                ToolCalls = message.ToolCalls == null
                    ? new List<ToolCall>()
                    : message.ToolCalls.Select(item => new ToolCall
                    {
                        Id = item.Id,
                        Name = item.Name,
                        Input = item.Input,
                        Description = item.Description
                    }).ToList(),
                ToolName = message.ToolName,
                RawToolInput = message.RawToolInput,
                ToolSuccess = message.ToolSuccess
            };
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxLength) + "...";
        }

        private static int ResolveMaxIterations(AgentRunOptions options)
        {
            var configured = options.MaxIterations > 0 ? options.MaxIterations : AskModeMaxIterations;
            if (options.Mode == AgentMode.Ask)
            {
                return Math.Min(AskModeMaxIterations, configured);
            }

            return configured;
        }
    }
}
