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
    /// <summary>
    /// 统一负责写步骤验证计划、验证工具执行和写步骤事件构造。
    /// 主编排器只决定这些结果在总事件流中的发送顺序。
    /// </summary>
    internal sealed class WriteStepCoordinator
    {
        private static readonly TimeSpan ToolExecutionTimeout = TimeSpan.FromSeconds(30);
        private const int ToolErrorMessageMaxLength = 500;

        private readonly IToolRegistry _toolRegistry;
        private readonly IConversationStore _conversationStore;

        internal WriteStepCoordinator(
            IToolRegistry toolRegistry,
            IConversationStore conversationStore)
        {
            _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
            _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        }

        internal AutoVerifyPlan BuildAutoVerifyPlan(string toolName, JObject parsedInput)
        {
            switch ((toolName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "patch_range":
                    return BuildPatchRangeAutoVerifyPlan(parsedInput);
                case "execute_script":
                    return BuildExecuteScriptAutoVerifyPlan(parsedInput);
                default:
                    return AutoVerifyPlan.Unsupported("当前工具类型不支持系统写后验证。");
            }
        }

        private static AutoVerifyPlan BuildPatchRangeAutoVerifyPlan(JObject parsedInput)
        {
            if (parsedInput == null)
            {
                return AutoVerifyPlan.Unsupported("patch_range 缺少结构化输入，无法生成写后验证步骤。");
            }

            PatchRangeRequest request;
            using (var inputDocument = JsonDocument.Parse(parsedInput.ToString(Formatting.None)))
            {
                request = PatchRangeRequest.Parse(inputDocument.RootElement);
            }

            if (request.Operations.Count == 0)
            {
                return AutoVerifyPlan.Unsupported("patch_range 未提供可验证的 operations。");
            }

            var checks = new JArray();
            foreach (var operation in request.Operations)
            {
                switch ((operation.Type ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "replace_text":
                        checks.Add(new JObject
                        {
                            ["type"] = "text_equals",
                            ["paragraph_index"] = operation.ParagraphIndex,
                            ["expected"] = NormalizeAutoVerifyText(operation.Text)
                        });
                        break;
                    case "insert_paragraph_after":
                        checks.Add(new JObject
                        {
                            ["type"] = "paragraph_exists",
                            ["paragraph_index"] = operation.ParagraphIndex + 1,
                            ["should_exist"] = true
                        });
                        checks.Add(new JObject
                        {
                            ["type"] = "text_equals",
                            ["paragraph_index"] = operation.ParagraphIndex + 1,
                            ["expected"] = NormalizeAutoVerifyText(operation.Text)
                        });
                        if (!string.IsNullOrWhiteSpace(operation.Style))
                        {
                            checks.Add(new JObject
                            {
                                ["type"] = "style_equals",
                                ["paragraph_index"] = operation.ParagraphIndex + 1,
                                ["expected"] = operation.Style
                            });
                        }

                        break;
                    case "set_paragraph_style":
                        if (string.IsNullOrWhiteSpace(operation.Style))
                        {
                            return AutoVerifyPlan.Unsupported("set_paragraph_style 缺少 style，无法生成写后验证步骤。");
                        }

                        checks.Add(new JObject
                        {
                            ["type"] = "style_equals",
                            ["paragraph_index"] = operation.ParagraphIndex,
                            ["expected"] = operation.Style
                        });
                        break;
                    case "delete_paragraph":
                        return AutoVerifyPlan.Unsupported("delete_paragraph 暂不支持可靠的系统写后验证，请改用 execute_script 并显式提供 verify_code。");
                    default:
                        return AutoVerifyPlan.Unsupported("存在当前版本不支持系统写后验证的 patch_range 操作类型：" + operation.Type);
                }
            }

            if (checks.Count == 0)
            {
                return AutoVerifyPlan.Unsupported("当前写步骤未生成任何可执行的验证脚本。");
            }

            return AutoVerifyPlan.Supported(
                "verify_script",
                BuildPatchRangeAutoVerifyInput(checks),
                "系统正在执行当前写步骤的验证。");
        }

        private static AutoVerifyPlan BuildExecuteScriptAutoVerifyPlan(JObject parsedInput)
        {
            if (parsedInput == null)
            {
                return AutoVerifyPlan.Unsupported("execute_script 缺少结构化输入，无法生成写后验证步骤。");
            }

            var verifyCode = parsedInput.Value<string>("verify_code");
            if (string.IsNullOrWhiteSpace(verifyCode))
            {
                return AutoVerifyPlan.Unsupported("execute_script 未提供 verify_code，系统无法执行当前写步骤的验证。");
            }

            return AutoVerifyPlan.Supported(
                "verify_script",
                new JObject
                {
                    ["description"] = "验证当前脚本写步骤是否生效。",
                    ["code"] = verifyCode
                }.ToString(Formatting.None),
                "系统正在执行当前脚本写步骤的验证。");
        }

        internal async Task<AutoVerifyOutcome> ExecuteAutoVerifyAsync(
            PendingWriteStep pendingWriteStep,
            IUndoScope undoScope,
            CancellationToken cancellationToken)
        {
            if (pendingWriteStep == null)
            {
                throw new ArgumentNullException(nameof(pendingWriteStep));
            }

            if (pendingWriteStep.State != PendingWriteState.AwaitingVerification)
            {
                var failureMessage = "当前写步骤不处于待验证状态，无法自动补验证。";
                return AutoVerifyOutcome.CreateFailed(
                    failureMessage,
                    "当前写步骤状态异常，任务已中止。");
            }

            if (!pendingWriteStep.HasAutoVerifyPlan)
            {
                return AutoVerifyOutcome.CreateFailed(
                    pendingWriteStep.VerificationFailureReason,
                    "当前写步骤缺少可执行的验证输入，当前步骤待修复。");
            }

            var verifyTool = _toolRegistry.GetTool(pendingWriteStep.VerificationToolName);
            if (verifyTool == null)
            {
                var failureMessage = "系统未找到内部验证工具实现，当前步骤待修复。";
                return AutoVerifyOutcome.CreateFailed(
                    failureMessage,
                    "系统内部验证工具不可用，当前步骤待修复。");
            }

            var autoVerifyCall = new ToolCall
            {
                Id = pendingWriteStep.ToolCallId + "__auto_verify",
                Name = pendingWriteStep.VerificationToolName,
                Input = pendingWriteStep.VerificationInput,
                Description = pendingWriteStep.VerificationOperationDescription
            };

            ToolCallResult executionResult;
            try
            {
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeoutCts.CancelAfter(ToolExecutionTimeout);
                    using (var inputDocument = JsonDocument.Parse(pendingWriteStep.VerificationInput))
                    {
                        var toolTask = verifyTool.ExecuteAsync(
                            inputDocument.RootElement.Clone(),
                            undoScope,
                            timeoutCts.Token);
                        var completedTask = await Task.WhenAny(
                                toolTask,
                                Task.Delay(ToolExecutionTimeout, cancellationToken))
                            .ConfigureAwait(false);
                        executionResult = completedTask == toolTask
                            ? await toolTask.ConfigureAwait(false)
                            : ToolCallResult.Error(autoVerifyCall.Name, "工具执行超时。");
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                executionResult = ToolCallResult.Error(autoVerifyCall.Name, "工具执行超时。");
            }
            catch (Exception ex)
            {
                executionResult = ToolCallResult.Error(
                    autoVerifyCall.Name,
                    Truncate(ex.ToString(), ToolErrorMessageMaxLength));
            }

            var verificationFailureMessage = BuildVerificationFailureMessage(executionResult);
            if (executionResult.Success && TryGetVerificationAllPassed(executionResult.Output, out var allPassed) && allPassed)
            {
                return AutoVerifyOutcome.CreatePassed(
                    autoVerifyCall,
                    executionResult,
                    pendingWriteStep.VerificationOperationDescription);
            }

            return AutoVerifyOutcome.CreateFailed(
                verificationFailureMessage,
                "当前写步骤的验证未通过，当前步骤待修复。",
                autoVerifyCall,
                executionResult,
                pendingWriteStep.VerificationOperationDescription);
        }

        /// <summary>
        /// 根据自动验证结果原子地提交或回滚当前 UndoScope，并返回主循环需要继续协调的写步骤状态。
        /// </summary>
        internal WriteStepTransition ApplyVerificationOutcome(
            PendingWriteStep executedWriteStep,
            AutoVerifyOutcome outcome,
            IUndoScope undoScope)
        {
            if (executedWriteStep == null)
            {
                throw new ArgumentNullException(nameof(executedWriteStep));
            }

            if (outcome == null || !outcome.Passed)
            {
                undoScope?.Rollback();
                var failureMessage = outcome == null
                    ? "写步骤已执行，但验证步骤未返回结果，当前步骤待修复。"
                    : outcome.FailureMessage;
                return WriteStepTransition.RolledBack(
                    executedWriteStep.MarkRepairRequired(failureMessage));
            }

            undoScope?.Commit();
            return WriteStepTransition.Committed();
        }

        internal async Task AppendAutoVerifyObservationAsync(
            string documentPath,
            IList<AgentMessage> messages,
            PendingWriteStep pendingWriteStep,
            AutoVerifyOutcome outcome,
            AutoVerifyObservationDisposition disposition,
            CancellationToken cancellationToken)
        {
            var observation = BuildAutoVerifyObservationMessage(
                pendingWriteStep,
                outcome,
                disposition);

            if (string.IsNullOrWhiteSpace(observation))
            {
                return;
            }

            // 自动验证属于系统内部观察，必须以普通用户消息进入上下文，避免产生孤立 tool 消息。
            var message = new AgentMessage
            {
                Role = "user",
                Content = observation.Trim(),
                IsInternalObservation = true,
                InternalObservationKind = "auto_verify_result"
            };

            await _conversationStore
                .AppendUserMessageAsync(documentPath, message, cancellationToken)
                .ConfigureAwait(false);
            messages.Add(CloneMessage(message));
        }

        private static string BuildAutoVerifyObservationMessage(
            PendingWriteStep pendingWriteStep,
            AutoVerifyOutcome outcome,
            AutoVerifyObservationDisposition disposition)
        {
            var autoVerifyCall = outcome == null ? null : outcome.ToolCall;
            var executionResult = outcome == null ? null : outcome.Result;
            var verificationMessage = outcome == null ? string.Empty : outcome.FailureMessage;
            var allPassed = executionResult != null
                && executionResult.Success
                && TryGetVerificationAllPassed(executionResult.Output, out var parsedAllPassed)
                && parsedAllPassed;
            var builder = new StringBuilder();
            builder.AppendLine("[SmartWord 自动验证结果]");
            var stepDescription = pendingWriteStep == null || string.IsNullOrWhiteSpace(pendingWriteStep.OperationDescription)
                ? "当前写步骤"
                : pendingWriteStep.OperationDescription.Trim();
            if (allPassed && disposition == AutoVerifyObservationDisposition.Committed)
            {
                builder.AppendLine($"当前写步骤“{stepDescription}”已自动验证通过且已提交。请继续执行后续 Todo，不要重复该步骤。");
                return builder.ToString();
            }

            builder.AppendLine("系统已在写操作后执行自动验证。这不是用户的新需求，而是当前写步骤的内部观察结果。");
            builder.AppendLine();
            builder.AppendLine("当前写步骤：");
            builder.AppendLine("- " + stepDescription);
            builder.AppendLine();
            builder.AppendLine("验证工具：");
            builder.AppendLine("- " + (autoVerifyCall == null || string.IsNullOrWhiteSpace(autoVerifyCall.Name)
                ? "未执行"
                : autoVerifyCall.Name.Trim()));
            builder.AppendLine();
            builder.AppendLine("验证状态：");
            if (disposition == AutoVerifyObservationDisposition.RolledBack)
            {
                builder.AppendLine("- 当前写步骤未通过验证，当前失败写步骤已回退，之前已验证通过的步骤保持不变。");
            }
            else if (executionResult == null)
            {
                builder.AppendLine("- 自动验证未能执行。");
            }
            else if (!executionResult.Success)
            {
                builder.AppendLine("- 验证工具执行失败。");
            }
            else
            {
                builder.AppendLine("- 验证工具已执行，但当前写步骤未通过验证。");
            }

            if (!string.IsNullOrWhiteSpace(verificationMessage))
            {
                builder.AppendLine();
                builder.AppendLine("验证结论：");
                builder.AppendLine(verificationMessage.Trim());
            }

            if (executionResult != null && !string.IsNullOrWhiteSpace(executionResult.Output))
            {
                builder.AppendLine();
                builder.AppendLine("验证输出：");
                builder.AppendLine(Truncate(executionResult.Output, 2000));
            }

            builder.AppendLine();
            builder.AppendLine("下一步要求：");
            builder.AppendLine("- 请基于上面的验证结论和验证输出修复当前步骤。");
            builder.AppendLine("- 修复时优先使用更小范围、更稳妥的写操作，不要重复已经完成的前序 Todo。");
            builder.AppendLine("- 修复后仍必须通过验证。");
            return builder.ToString();
        }

        private static string BuildPatchRangeAutoVerifyInput(JArray checks)
        {
            var codeBuilder = new StringBuilder();
            codeBuilder.AppendLine("var results = new List<object>();");
            codeBuilder.AppendLine("bool allPassed = true;");
            codeBuilder.AppendLine("dynamic paragraphs = ActiveDoc == null ? null : ActiveDoc.Paragraphs;");
            codeBuilder.AppendLine("int paragraphCount = paragraphs == null ? 0 : Convert.ToInt32(paragraphs.Count);");
            codeBuilder.AppendLine("bool ParagraphExists(int index) { return index >= 0 && index < paragraphCount; }");
            codeBuilder.AppendLine("string NormalizeText(string text) { return string.IsNullOrEmpty(text) ? string.Empty : text.Replace(\"\\r\", string.Empty).Replace(\"\\a\", string.Empty).Trim(); }");
            codeBuilder.AppendLine("string ReadParagraphText(int index) { if (!ParagraphExists(index)) { return string.Empty; } dynamic paragraph = paragraphs[index + 1]; dynamic range = paragraph == null ? null : paragraph.Range; return NormalizeText(range == null ? string.Empty : Convert.ToString(range.Text)); }");
            codeBuilder.AppendLine("string ReadParagraphStyle(int index) { if (!ParagraphExists(index)) { return string.Empty; } dynamic paragraph = paragraphs[index + 1]; dynamic style = null; try { style = paragraph == null ? null : paragraph.get_Style(); if (style == null) { return string.Empty; } try { return Convert.ToString(style.NameLocal); } catch { return Convert.ToString(style); } } catch { return string.Empty; } }");
            codeBuilder.AppendLine("void AddResult(string checkKey, bool passed, string actual, string expected, string hint) { results.Add(new { check_key = checkKey, passed = passed, actual = actual, expected = expected, hint = passed ? string.Empty : hint }); if (!passed) { allPassed = false; } }");

            for (var index = 0; index < checks.Count; index++)
            {
                if (!(checks[index] is JObject check))
                {
                    continue;
                }

                AppendPatchRangeCheckScript(codeBuilder, check, index);
            }

            codeBuilder.AppendLine("return new { all_passed = allPassed, results = results };");

            return new JObject
            {
                ["description"] = "验证当前 patch_range 写步骤是否生效。",
                ["code"] = codeBuilder.ToString()
            }.ToString(Formatting.None);
        }

        private static void AppendPatchRangeCheckScript(StringBuilder builder, JObject check, int index)
        {
            var type = (check.Value<string>("type") ?? string.Empty).Trim().ToLowerInvariant();
            var paragraphIndex = check.Value<int?>("paragraph_index") ?? -1;
            var expected = check.Value<string>("expected") ?? string.Empty;
            var shouldExist = check.Value<bool?>("should_exist") ?? true;
            var checkKey = type + "_" + index;

            switch (type)
            {
                case "text_contains":
                    builder.AppendLine("{");
                    builder.AppendLine("    var actual = ReadParagraphText(" + paragraphIndex + ");");
                    builder.AppendLine("    var exists = ParagraphExists(" + paragraphIndex + ");");
                    builder.AppendLine("    var expected = " + JsonConvert.ToString(expected) + ";");
                    builder.AppendLine("    var passed = exists && !string.IsNullOrEmpty(expected) && actual.IndexOf(expected, StringComparison.Ordinal) >= 0;");
                    builder.AppendLine("    AddResult(" + JsonConvert.ToString(checkKey) + ", passed, actual, expected, \"文本未包含预期内容，建议先回读目标段落，再检查是否写入到了错误位置。\");");
                    builder.AppendLine("}");
                    break;
                case "text_equals":
                    builder.AppendLine("{");
                    builder.AppendLine("    var actual = ReadParagraphText(" + paragraphIndex + ");");
                    builder.AppendLine("    var exists = ParagraphExists(" + paragraphIndex + ");");
                    builder.AppendLine("    var expected = " + JsonConvert.ToString(expected) + ";");
                    builder.AppendLine("    var passed = exists && string.Equals(actual, expected, StringComparison.Ordinal);");
                    builder.AppendLine("    AddResult(" + JsonConvert.ToString(checkKey) + ", passed, actual, expected, \"文本与预期不完全一致，建议检查是否残留了原有内容或换行。\");");
                    builder.AppendLine("}");
                    break;
                case "text_not_contains":
                    builder.AppendLine("{");
                    builder.AppendLine("    var actual = ReadParagraphText(" + paragraphIndex + ");");
                    builder.AppendLine("    var exists = ParagraphExists(" + paragraphIndex + ");");
                    builder.AppendLine("    var expected = " + JsonConvert.ToString(expected) + ";");
                    builder.AppendLine("    var passed = exists && (string.IsNullOrEmpty(expected) || actual.IndexOf(expected, StringComparison.Ordinal) < 0);");
                    builder.AppendLine("    AddResult(" + JsonConvert.ToString(checkKey) + ", passed, actual, expected, \"目标文本仍然存在，建议改用更精确的范围写入或补充删除操作。\");");
                    builder.AppendLine("}");
                    break;
                case "style_equals":
                    builder.AppendLine("{");
                    builder.AppendLine("    var actual = ReadParagraphStyle(" + paragraphIndex + ");");
                    builder.AppendLine("    var exists = ParagraphExists(" + paragraphIndex + ");");
                    builder.AppendLine("    var expected = " + JsonConvert.ToString(expected) + ";");
                    builder.AppendLine("    var passed = exists && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);");
                    builder.AppendLine("    AddResult(" + JsonConvert.ToString(checkKey) + ", passed, actual, expected, \"段落样式未达到预期，建议确认样式名称是否与 Word 中的本地样式名一致。\");");
                    builder.AppendLine("}");
                    break;
                case "paragraph_exists":
                    builder.AppendLine("{");
                    builder.AppendLine("    var exists = ParagraphExists(" + paragraphIndex + ");");
                    builder.AppendLine("    var actual = exists ? \"true\" : \"false\";");
                    builder.AppendLine("    var expected = " + JsonConvert.ToString(shouldExist ? "true" : "false") + ";");
                    builder.AppendLine("    var passed = exists == " + (shouldExist ? "true" : "false") + ";");
                    builder.AppendLine("    AddResult(" + JsonConvert.ToString(checkKey) + ", passed, actual, expected, " + JsonConvert.ToString(shouldExist ? "目标段落不存在，建议先确认段落索引是否仍然有效。" : "目标段落仍然存在，删除操作可能没有真正命中段落标记。") + ");");
                    builder.AppendLine("}");
                    break;
            }
        }

        private static string NormalizeAutoVerifyText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text
                .Replace("\r", string.Empty)
                .Replace("\a", string.Empty)
                .Trim();
        }

        internal static AgentEvent CreateChangeEvent(
            AgentEventType eventType,
            PendingWriteStep pendingWriteStep,
            string message,
            string toolOutput = null)
        {
            if (pendingWriteStep == null)
            {
                throw new ArgumentNullException(nameof(pendingWriteStep));
            }

            return new AgentEvent
            {
                Type = eventType,
                ToolCallId = pendingWriteStep.ToolCallId,
                ToolName = pendingWriteStep.ToolName,
                ToolOutput = toolOutput ?? string.Empty,
                AffectedParagraphs = pendingWriteStep.AffectedParagraphs,
                OperationDescription = pendingWriteStep.OperationDescription,
                Message = message ?? string.Empty
            };
        }

        internal static AgentEvent CreatePendingWriteTerminationEvent(PendingWriteStep pendingWriteStep)
        {
            if (pendingWriteStep == null)
            {
                throw new ArgumentNullException(nameof(pendingWriteStep));
            }

            if (pendingWriteStep.State == PendingWriteState.AwaitingVerification)
            {
                return CreatePendingWriteErrorEvent(
                    pendingWriteStep,
                    "写步骤已执行，但任务在验证步骤完成前结束，系统已停止任务并回滚未确认写入。");
            }

            return CreatePendingWriteErrorEvent(
                pendingWriteStep,
                "写步骤失败后未完成修复，系统已停止任务并回滚本次任务中的写入。");
        }

        internal static AgentEvent CreatePendingWriteStateEvent(PendingWriteStep pendingWriteStep)
        {
            if (pendingWriteStep == null)
            {
                throw new ArgumentNullException(nameof(pendingWriteStep));
            }

            if (pendingWriteStep.State == PendingWriteState.AwaitingVerification)
            {
                return CreateChangeEvent(
                    AgentEventType.ChangeUnverified,
                    pendingWriteStep,
                    "写步骤已执行，但任务在验证步骤完成前结束，当前步骤未被确认。");
            }

            return CreateChangeEvent(
                AgentEventType.ChangeRepairRequired,
                pendingWriteStep,
                string.IsNullOrWhiteSpace(pendingWriteStep.LastFailureMessage)
                    ? "写步骤失败后仍待修复。"
                    : pendingWriteStep.LastFailureMessage);
        }

        private static AgentEvent CreatePendingWriteErrorEvent(PendingWriteStep pendingWriteStep, string message)
        {
            if (pendingWriteStep == null)
            {
                throw new ArgumentNullException(nameof(pendingWriteStep));
            }

            return new AgentEvent
            {
                Type = AgentEventType.Error,
                ToolCallId = pendingWriteStep.ToolCallId,
                ToolName = pendingWriteStep.ToolName,
                AffectedParagraphs = pendingWriteStep.AffectedParagraphs,
                OperationDescription = pendingWriteStep.OperationDescription,
                Message = message ?? string.Empty
            };
        }

        private static bool TryGetVerificationAllPassed(string output, out bool allPassed)
        {
            allPassed = false;
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            try
            {
                var payload = JObject.Parse(output);
                var allPassedToken = payload["all_passed"];
                if (allPassedToken == null || allPassedToken.Type == JTokenType.Null)
                {
                    return false;
                }

                allPassed = allPassedToken.Value<bool>();
                return true;
            }
            catch (JsonReaderException)
            {
                return false;
            }
        }

        private static string BuildVerificationFailureMessage(ToolCallResult verificationResult)
        {
            if (verificationResult == null)
            {
                return "写步骤已执行，但验证步骤未返回结果，当前步骤待修复。";
            }

            if (!verificationResult.Success)
            {
                return "写步骤已执行，但验证步骤执行失败，当前步骤待修复。";
            }

            if (!TryGetVerificationAllPassed(verificationResult.Output, out var allPassed))
            {
                return "写步骤已执行，但验证步骤返回结果无法判定，当前步骤待修复。";
            }

            return allPassed
                ? "已通过验证步骤确认改动生效。"
                : "写步骤已执行，但验证步骤未全部通过，当前步骤待修复。";
        }

    }

    /// <summary>
    /// 描述一次自动验证后写步骤和 UndoScope 的确定性状态转换。
    /// </summary>
    internal sealed class WriteStepTransition
    {
        internal bool Passed { get; private set; }

        internal bool UndoCommitted { get; private set; }

        internal bool UndoRolledBack { get; private set; }

        internal PendingWriteStep PendingWriteStep { get; private set; }

        internal static WriteStepTransition Committed()
        {
            return new WriteStepTransition
            {
                Passed = true,
                UndoCommitted = true
            };
        }

        internal static WriteStepTransition RolledBack(PendingWriteStep pendingWriteStep)
        {
            return new WriteStepTransition
            {
                Passed = false,
                UndoRolledBack = true,
                PendingWriteStep = pendingWriteStep
            };
        }
    }
}
