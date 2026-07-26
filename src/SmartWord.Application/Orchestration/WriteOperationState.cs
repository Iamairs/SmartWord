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

namespace SmartWord.Application.Orchestration
{
    internal static class WriteOperationState
    {
        /// <summary>
        /// 将写工具错误压缩为可供状态机和用户理解的单行修复提示。
        /// </summary>
        internal static string BuildWriteRepairMessage(ToolCallResult executionResult)
        {
            if (executionResult == null || string.IsNullOrWhiteSpace(executionResult.Output))
            {
                return "写步骤执行失败，当前步骤待修复。";
            }

            var normalized = executionResult.Output.Replace("\r\n", "\n");
            var firstLine = normalized
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return "写步骤执行失败，当前步骤待修复。";
            }

            return "写步骤执行失败，当前步骤待修复：" + firstLine;
        }

        internal enum PendingWriteState
        {
            AwaitingVerification = 0,
            RepairRequired = 1
        }

        internal enum AutoVerifyObservationDisposition
        {
            Committed = 0,
            RolledBack = 1
        }

        internal sealed class AutoVerifyPlan
        {
            public string ToolName { get; private set; } = string.Empty;

            public string InputJson { get; private set; } = string.Empty;

            public string OperationDescription { get; private set; } = string.Empty;

            public string FailureReason { get; private set; } = string.Empty;

            public bool IsSupported => !string.IsNullOrWhiteSpace(InputJson);

            public static AutoVerifyPlan Supported(string toolName, string inputJson, string operationDescription)
            {
                return new AutoVerifyPlan
                {
                    ToolName = toolName ?? string.Empty,
                    InputJson = inputJson ?? string.Empty,
                    OperationDescription = operationDescription ?? string.Empty
                };
            }

            public static AutoVerifyPlan Unsupported(string failureReason)
            {
                return new AutoVerifyPlan
                {
                    FailureReason = failureReason ?? string.Empty
                };
            }
        }

        internal sealed class AutoVerifyOutcome
        {
            public bool Passed { get; private set; }

            public string FailureMessage { get; private set; } = string.Empty;

            public string TerminationMessage { get; private set; } = string.Empty;

            public ToolCall ToolCall { get; private set; }

            public ToolCallResult Result { get; private set; }

            public string OperationDescription { get; private set; } = string.Empty;

            public static AutoVerifyOutcome CreatePassed(
                ToolCall toolCall,
                ToolCallResult result,
                string operationDescription)
            {
                return new AutoVerifyOutcome
                {
                    Passed = true,
                    ToolCall = toolCall,
                    Result = result,
                    OperationDescription = operationDescription ?? string.Empty
                };
            }

            public static AutoVerifyOutcome CreateFailed(
                string failureMessage,
                string terminationMessage,
                ToolCall toolCall = null,
                ToolCallResult result = null,
                string operationDescription = null)
            {
                return new AutoVerifyOutcome
                {
                    Passed = false,
                    FailureMessage = failureMessage ?? string.Empty,
                    TerminationMessage = terminationMessage ?? string.Empty,
                    ToolCall = toolCall,
                    Result = result,
                    OperationDescription = operationDescription ?? string.Empty
                };
            }
        }

        /// <summary>
        /// 记录当前写步骤的状态，确保编排层在验证通过前不会进入下一独立写步骤。
        /// </summary>
        internal sealed class PendingWriteStep
        {
            public string ToolCallId { get; private set; }

            public string ToolName { get; private set; }

            public int[] AffectedParagraphs { get; private set; }

            public string OperationDescription { get; private set; }

            public PendingWriteState State { get; private set; }

            public int RepairAttempts { get; private set; }

            public string LastFailureMessage { get; private set; } = string.Empty;

            public string VerificationToolName { get; private set; } = string.Empty;

            public string VerificationInput { get; private set; } = string.Empty;

            public string VerificationOperationDescription { get; private set; } = string.Empty;

            public string VerificationFailureReason { get; private set; } = string.Empty;

            public bool HasAutoVerifyPlan => !string.IsNullOrWhiteSpace(VerificationToolName)
                && !string.IsNullOrWhiteSpace(VerificationInput);

            public static PendingWriteStep CreateAwaitingVerification(
                ToolCall toolCall,
                ToolCallResult result,
                AutoVerifyPlan autoVerifyPlan)
            {
                return new PendingWriteStep
                {
                    ToolCallId = toolCall == null ? string.Empty : toolCall.Id ?? string.Empty,
                    ToolName = toolCall == null ? string.Empty : toolCall.Name ?? string.Empty,
                    AffectedParagraphs = result?.AffectedParagraphs,
                    OperationDescription = result?.OperationDescription ?? string.Empty,
                    State = PendingWriteState.AwaitingVerification,
                    VerificationToolName = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.ToolName,
                    VerificationInput = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.InputJson,
                    VerificationOperationDescription = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.OperationDescription,
                    VerificationFailureReason = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.FailureReason
                };
            }

            public static PendingWriteStep CreateRepairRequired(
                ToolCall toolCall,
                ToolCallResult result,
                string operationDescription)
            {
                return new PendingWriteStep
                {
                    ToolCallId = toolCall == null ? string.Empty : toolCall.Id ?? string.Empty,
                    ToolName = toolCall == null ? string.Empty : toolCall.Name ?? string.Empty,
                    AffectedParagraphs = result?.AffectedParagraphs,
                    OperationDescription = string.IsNullOrWhiteSpace(result?.OperationDescription)
                        ? operationDescription ?? string.Empty
                        : result.OperationDescription,
                    State = PendingWriteState.RepairRequired,
                    RepairAttempts = 1,
                    LastFailureMessage = BuildWriteRepairMessage(result),
                    VerificationToolName = string.Empty,
                    VerificationInput = string.Empty,
                    VerificationOperationDescription = string.Empty,
                    VerificationFailureReason = string.Empty
                };
            }

            public PendingWriteStep MarkRepairRequired(string failureMessage)
            {
                return new PendingWriteStep
                {
                    ToolCallId = ToolCallId,
                    ToolName = ToolName,
                    AffectedParagraphs = AffectedParagraphs,
                    OperationDescription = OperationDescription,
                    State = PendingWriteState.RepairRequired,
                    RepairAttempts = RepairAttempts,
                    LastFailureMessage = failureMessage ?? string.Empty,
                    VerificationToolName = VerificationToolName,
                    VerificationInput = VerificationInput,
                    VerificationOperationDescription = VerificationOperationDescription,
                    VerificationFailureReason = VerificationFailureReason
                };
            }

            public PendingWriteStep MarkWriteExecuted(
                ToolCall toolCall,
                ToolCallResult result,
                AutoVerifyPlan autoVerifyPlan)
            {
                return new PendingWriteStep
                {
                    ToolCallId = toolCall == null ? ToolCallId : toolCall.Id ?? string.Empty,
                    ToolName = toolCall == null ? ToolName : toolCall.Name ?? string.Empty,
                    AffectedParagraphs = result?.AffectedParagraphs ?? AffectedParagraphs,
                    OperationDescription = string.IsNullOrWhiteSpace(result?.OperationDescription)
                        ? OperationDescription
                        : result.OperationDescription,
                    State = PendingWriteState.AwaitingVerification,
                    RepairAttempts = RepairAttempts,
                    LastFailureMessage = string.Empty,
                    VerificationToolName = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.ToolName,
                    VerificationInput = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.InputJson,
                    VerificationOperationDescription = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.OperationDescription,
                    VerificationFailureReason = autoVerifyPlan == null ? string.Empty : autoVerifyPlan.FailureReason
                };
            }

            public PendingWriteStep RegisterWriteFailure(
                ToolCall toolCall,
                ToolCallResult result,
                string operationDescription)
            {
                return new PendingWriteStep
                {
                    ToolCallId = toolCall == null ? ToolCallId : toolCall.Id ?? string.Empty,
                    ToolName = toolCall == null ? ToolName : toolCall.Name ?? string.Empty,
                    AffectedParagraphs = result?.AffectedParagraphs ?? AffectedParagraphs,
                    OperationDescription = string.IsNullOrWhiteSpace(result?.OperationDescription)
                        ? (string.IsNullOrWhiteSpace(OperationDescription) ? operationDescription ?? string.Empty : OperationDescription)
                        : result.OperationDescription,
                    State = PendingWriteState.RepairRequired,
                    RepairAttempts = RepairAttempts + 1,
                    LastFailureMessage = BuildWriteRepairMessage(result),
                    VerificationToolName = VerificationToolName,
                    VerificationInput = VerificationInput,
                    VerificationOperationDescription = VerificationOperationDescription,
                    VerificationFailureReason = VerificationFailureReason
                };
            }
        }
    }
}

