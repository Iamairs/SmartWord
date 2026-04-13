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
        private static readonly TimeSpan ToolExecutionTimeout = TimeSpan.FromSeconds(30);
        private const int ToolErrorMessageMaxLength = 500;
        private const int CompactionThreshold = 80000;

        private readonly ILlmClient _llmClient;
        private readonly IContextHydrator _contextHydrator;
        private readonly IConversationStore _conversationStore;
        private readonly SystemPromptBuilder _systemPromptBuilder;
        private readonly IToolRegistry _toolRegistry;
        private readonly PermissionGuard _permissionGuard;

        public AgentOrchestrator(
            ILlmClient llmClient,
            IContextHydrator contextHydrator,
            IConversationStore conversationStore,
            SystemPromptBuilder systemPromptBuilder,
            IToolRegistry toolRegistry,
            PermissionGuard permissionGuard)
        {
            _llmClient = llmClient;
            _contextHydrator = contextHydrator;
            _conversationStore = conversationStore;
            _systemPromptBuilder = systemPromptBuilder;
            _toolRegistry = toolRegistry;
            _permissionGuard = permissionGuard;
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
            // 兜底运行参数，避免空引用。
            var safeOptions = options ?? new AgentRunOptions();

            // 首次水合文档上下文，用于确定对话归属文档。
            var documentContext = await _contextHydrator.HydrateAsync(cancellationToken).ConfigureAwait(false);
            var documentPath = string.IsNullOrWhiteSpace(documentContext.DocumentPath)
                ? "__active_document__"
                : documentContext.DocumentPath;

            // 构造当前用户消息并写入会话存储。
            var userMessage = new AgentMessage
            {
                Role = "user",
                Content = userInput ?? string.Empty
            };

            // 
            await _conversationStore
                .AppendUserMessageAsync(documentPath, userMessage, cancellationToken)
                .ConfigureAwait(false);

            // 读取该文档对应的历史消息。
            var history = await _conversationStore
                .GetHistoryAsync(documentPath, cancellationToken)
                .ConfigureAwait(false);

            // 组装发送给模型的消息序列（system + history）。
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

            // 预取本次模式可用工具定义；初始化引用映射状态。
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

            // 计算最大迭代次数（Ask 模式受上限约束）。
            var maxIterations = ResolveMaxIterations(safeOptions);
            AgentMessage finalAssistantMessage = null;

            // 主循环：每轮包含一次 LLM 回复 + 若干工具调用。
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 每轮校验当前文档是否发生切换，防止跨文档误操作。
                var latestContext = await _contextHydrator.HydrateAsync(cancellationToken).ConfigureAwait(false);
                var latestDocumentPath = string.IsNullOrWhiteSpace(latestContext.DocumentPath)
                    ? "__active_document__"
                    : latestContext.DocumentPath;
                if (!string.Equals(latestDocumentPath, documentPath, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new AgentEvent
                    {
                        Type = AgentEventType.DocumentMismatch,
                        Message = "当前文档已切换，任务已取消。请返回原文档后重新发起。"
                    };

                    yield break;
                }

                // 会话接近上下文上限时提前终止，避免超出模型输入限制。
                if (_conversationStore.EstimateTokenCount(messages) > Math.Max(CompactionThreshold, safeOptions.CompactionThreshold))
                {
                    yield return new AgentEvent
                    {
                        Type = AgentEventType.ContextCompacted,
                        Message = "当前对话已接近上下文上限，Phase 2 先停止本轮并保留已有结果。"
                    };

                    break;
                }

                // 调用模型：有工具定义时走 tool-calling 接口，否则走纯流式文本接口。
                AgentMessage assistantMessage;
                if (toolDefinitions.Count > 0)
                {
                    var chunks = new ConcurrentQueue<string>();
                    using (var signal = new SemaphoreSlim(0))
                    {
                        // 后台请求模型，回调中持续推入流式片段。
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

                        // 前台持续消费片段并向上游透传 StreamChunk 事件。
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

                        // 清空尾部残留片段。
                        while (chunks.TryDequeue(out var remainingChunk))
                        {
                            yield return new AgentEvent
                            {
                                Type = AgentEventType.StreamChunk,
                                Content = remainingChunk
                            };
                        }

                        // 获取最终 assistant 消息（可能包含 tool calls）。
                        assistantMessage = await assistantTask.ConfigureAwait(false);
                    }
                }
                else
                {
                    // 无工具模式：仅拼接流式文本为最终 assistant 内容。
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

                // 保存本轮 assistant 消息到会话存储和内存消息列表。
                finalAssistantMessage = assistantMessage;
                await _conversationStore
                    .AppendAssistantMessageAsync(documentPath, assistantMessage, cancellationToken)
                    .ConfigureAwait(false);
                messages.Add(CloneMessage(assistantMessage));

                // 若模型未请求工具，说明本轮可直接结束。
                if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
                {
                    break;
                }

                // 单轮工具调用数量限流，超出则截断并写入系统提示。
                var toolCalls = assistantMessage.ToolCalls;
                if (toolCalls.Count > MaxToolCallsPerIteration)
                {
                    toolCalls = toolCalls.Take(MaxToolCallsPerIteration).ToList();
                    Log.Warning(
                        "本轮工具调用数量超过限制，已截断。MaxToolCallsPerIteration={MaxToolCallsPerIteration}",
                        MaxToolCallsPerIteration);
                }

                // 顺序执行工具调用，避免复杂并发状态冲突。
                foreach (var toolCall in toolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 通知上游：工具开始执行。
                    yield return new AgentEvent
                    {
                        Type = AgentEventType.ToolCallStarted,
                        ToolCallId = toolCall.Id,
                        ToolName = toolCall.Name,
                        ToolInput = toolCall.Input ?? string.Empty
                    };

                    // 权限校验失败则返回 denied 结果，不进入真实执行。
                    if (!_permissionGuard.IsAllowed(toolCall.Name, safeOptions.Mode))
                    {
                        var deniedResult = ToolCallResult.Denied(toolCall.Name);
                        await AppendToolResultAsync(
                                documentPath,
                                messages,
                                toolCall,
                                deniedResult,
                                cancellationToken)
                            .ConfigureAwait(false);

                        yield return new AgentEvent
                        {
                            Type = AgentEventType.ToolCallDenied,
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.Name,
                            ToolInput = toolCall.Input ?? string.Empty,
                            ToolOutput = deniedResult.Output,
                            ToolSuccess = deniedResult.Success
                        };

                        continue;
                    }

                    ToolCallResult executionResult;

                    // 先解析工具输入 JSON；解析失败直接返回错误结果。
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

                    if (inputParseError != null)
                    {
                        await AppendToolResultAsync(
                                documentPath,
                                messages,
                                toolCall,
                                inputParseError,
                                cancellationToken)
                            .ConfigureAwait(false);

                        yield return CreateToolCompletedEvent(toolCall, inputParseError);
                        continue;
                    }

                    // 执行工具：包含工具不存在、超时、异常等兜底处理。
                    try
                    {
                        var tool = _toolRegistry.GetTool(toolCall.Name);
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
                                        null,
                                        timeoutCts.Token);

                                    // 双重超时保护：工具 token + Task.Delay 竞速。
                                    var completedTask = await Task.WhenAny(toolTask, Task.Delay(ToolExecutionTimeout, cancellationToken))
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
                        // 非外部取消引发的取消，按工具超时处理。
                        executionResult = ToolCallResult.Error(toolCall.Name, "工具执行超时。");
                    }
                    catch (Exception ex)
                    {
                        // 其他异常统一截断后返回，避免过长错误污染上下文。
                        executionResult = ToolCallResult.Error(
                            toolCall.Name,
                            Truncate(ex.ToString(), ToolErrorMessageMaxLength));
                    }

                    // 对工具输出做引用装饰（paragraph -> ref），并更新映射系统消息。
                    executionResult.Output = DecorateToolOutput(
                        toolCall.Name,
                        executionResult.Output,
                        documentPath,
                        citationRegistry,
                        paragraphToRef,
                        ref nextCitationRef);

                    // 回写工具结果到存储和消息列表，供下一轮模型继续推理。
                    await AppendToolResultAsync(
                            documentPath,
                            messages,
                            toolCall,
                            executionResult,
                            cancellationToken)
                        .ConfigureAwait(false);

                    // 通知上游：工具执行完成。
                    yield return CreateToolCompletedEvent(toolCall, executionResult);
                }
            }

            // 统一输出任务完成事件，并根据最终答案中的 [n] 提取可见引用列表。
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
                ParagraphRefs = result.ParagraphRefs
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
