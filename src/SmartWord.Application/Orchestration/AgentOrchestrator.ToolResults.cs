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
using SmartWord.Application.Todo;
using SmartWord.Application.Tools;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Core.Telemetry;
using SmartWord.OfficeIntegration.Tools;
using static SmartWord.Application.Orchestration.AgentEventFactory;
using static SmartWord.Application.Orchestration.AgentOrchestratorUtilities;
using static SmartWord.Application.Orchestration.WriteOperationState;

namespace SmartWord.Application.Orchestration
{
    public sealed partial class AgentOrchestrator
    {
        private async Task<ToolConfirmationDecision> WaitForToolConfirmationDecisionAsync(
            ToolCall toolCall,
            string eventToolInput,
            string operationDescription,
            SkillScriptApprovalKey scriptApprovalKey,
            CancellationToken cancellationToken)
        {
            var extendedChannel = _confirmationChannel as IToolConfirmationChannel;
            if (extendedChannel != null)
            {
                return await extendedChannel
                    .WaitForConfirmationDecisionAsync(
                        new ToolConfirmationRequest
                        {
                            ToolCallId = toolCall == null ? string.Empty : toolCall.Id ?? string.Empty,
                            ToolName = toolCall == null ? string.Empty : toolCall.Name ?? string.Empty,
                            ToolInput = eventToolInput ?? string.Empty,
                            OperationDescription = operationDescription ?? string.Empty,
                            ScriptApprovalKey = scriptApprovalKey
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var confirmed = await _confirmationChannel
                .WaitForConfirmationAsync(toolCall == null ? string.Empty : toolCall.Id, cancellationToken)
                .ConfigureAwait(false);
            return ToolConfirmationDecision.FromBoolean(confirmed);
        }

        private async Task AppendToolResultAsync(
            string documentPath,
            IList<AgentMessage> messages,
            ToolCall toolCall,
            ToolCallResult result,
            CancellationToken cancellationToken,
            string taskRunId = null,
            string operationDescription = null)
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

            await _runAuditRecorder.TryRecordTaskToolAsync(
                    taskRunId,
                    toolCall,
                    result,
                    operationDescription,
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

        private static ConversationCompressionContext CreateCompressionContext(
            AgentRunOptions options,
            string documentPath,
            DocumentContext documentContext,
            TodoBoard currentTodoBoard,
            PendingWriteStep pendingWriteStep,
            IReadOnlyList<AgentMessage> messages)
        {
            var recentInternalObservations = messages == null
                ? new List<AgentMessage>()
                : messages
                    .Where(message => message != null && message.IsInternalObservation)
                    .Reverse()
                    .Take(5)
                    .Select(CloneMessage)
                    .Reverse()
                    .ToList();

            var latestRealUserMessage = messages == null
                ? null
                : messages.LastOrDefault(message =>
                    message != null
                    && string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                    && !message.IsInternalObservation);

            return new ConversationCompressionContext
            {
                Mode = options == null ? AgentMode.Ask : options.Mode,
                DocumentPath = documentPath ?? string.Empty,
                CurrentUserGoal = latestRealUserMessage == null
                    ? string.Empty
                    : latestRealUserMessage.Content ?? string.Empty,
                CurrentTodoBoard = currentTodoBoard,
                ActivePlan = options == null ? null : options.ActivePlan,
                PendingWriteStep = CreatePendingWriteStepSnapshot(pendingWriteStep),
                DocumentContext = documentContext,
                RecentInternalObservations = recentInternalObservations
            };
        }

        private static PendingWriteStepSnapshot CreatePendingWriteStepSnapshot(PendingWriteStep pendingWriteStep)
        {
            if (pendingWriteStep == null)
            {
                return null;
            }

            return new PendingWriteStepSnapshot
            {
                ToolCallId = pendingWriteStep.ToolCallId,
                ToolName = pendingWriteStep.ToolName,
                AffectedParagraphs = pendingWriteStep.AffectedParagraphs,
                OperationDescription = pendingWriteStep.OperationDescription,
                State = pendingWriteStep.State.ToString(),
                RepairAttempts = pendingWriteStep.RepairAttempts,
                LastFailureMessage = pendingWriteStep.LastFailureMessage,
                VerificationToolName = pendingWriteStep.VerificationToolName,
                VerificationOperationDescription = pendingWriteStep.VerificationOperationDescription,
                VerificationFailureReason = pendingWriteStep.VerificationFailureReason
            };
        }

        private async Task AppendInternalObservationAsync(
            string documentPath,
            IList<AgentMessage> messages,
            string content,
            CancellationToken cancellationToken)
        {
            // 系统内部观察不是模型发起的工具结果，必须用普通消息进入上下文，避免产生孤立 tool 消息。
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var message = new AgentMessage
            {
                Role = "user",
                Content = content.Trim(),
                IsInternalObservation = true,
                InternalObservationKind = "auto_verify_result"
            };

            await _conversationStore
                .AppendUserMessageAsync(documentPath, message, cancellationToken)
                .ConfigureAwait(false);

            messages.Add(CloneMessage(message));
        }

        private async Task AppendSkippedRemainingToolCallsAsync(
            string documentPath,
            IList<AgentMessage> messages,
            IReadOnlyList<ToolCall> toolCalls,
            int startIndex,
            string reason,
            CancellationToken cancellationToken)
        {
            if (toolCalls == null || startIndex < 0 || startIndex >= toolCalls.Count)
            {
                return;
            }

            for (var index = startIndex; index < toolCalls.Count; index++)
            {
                var skippedToolCall = toolCalls[index];
                var skippedResult = ToolCallResult.Skipped(
                    skippedToolCall.Name,
                    string.IsNullOrWhiteSpace(reason)
                        ? "当前轮次已提前结束，剩余工具调用已跳过。"
                        : reason);

                await AppendToolResultAsync(
                        documentPath,
                        messages,
                        skippedToolCall,
                        skippedResult,
                        cancellationToken)
                    .ConfigureAwait(false);

                Log.Information(
                    "已为剩余工具调用补齐 skipped 结果。ToolCallId={ToolCallId}, ToolName={ToolName}, Reason={Reason}",
                    skippedToolCall.Id,
                    skippedToolCall.Name,
                    reason);
            }
        }

        private async Task<SkillPromptContext> ResolveSkillPromptContextAsync(
            string userInput,
            AgentRunOptions options,
            CancellationToken cancellationToken)
        {
            if (_skillPromptResolver == null)
            {
                return new SkillPromptContext();
            }

            try
            {
                return await _skillPromptResolver
                    .ResolveAsync(userInput, options.SelectedSkillNames, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "加载 Skill prompt 上下文失败，本次任务将不使用 Skill。");
                return new SkillPromptContext();
            }
        }

        private string BuildSystemPrompt(
            AgentRunOptions options,
            DocumentContext documentContext,
            TodoBoard todoBoard,
            SkillPromptContext skillPromptContext)
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

            if (todoBoard != null && _todoRunCoordinator.IsAvailable)
            {
                contextBuilder.AppendLine();
                contextBuilder.AppendLine(_todoRunCoordinator.BuildPromptBlock(todoBoard));
                contextBuilder.AppendLine("Notice: 复杂任务应持续维护 todo board。计划变化时，先更新任务板再继续执行。");
            }

            if (skillPromptContext != null && !string.IsNullOrWhiteSpace(skillPromptContext.PromptBlock))
            {
                contextBuilder.AppendLine();
                contextBuilder.AppendLine(skillPromptContext.PromptBlock);
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

        private static bool TryGetTodoToolMetadata(ToolCallResult result, out TodoToolMetadata metadata)
        {
            metadata = result == null ? null : result.Metadata as TodoToolMetadata;
            return metadata != null && !string.IsNullOrWhiteSpace(metadata.BoardJson);
        }

        private static TodoBoard DeserializeTodoBoard(string boardJson)
        {
            if (string.IsNullOrWhiteSpace(boardJson))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<TodoBoard>(boardJson);
            }
            catch
            {
                return null;
            }
        }

        private static string SerializeCamelCase(object value)
        {
            return JsonConvert.SerializeObject(
                value,
                new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                    NullValueHandling = NullValueHandling.Ignore
                });
        }

    }
}
