using System;
using System.Linq;
using System.Text;
using SmartWord.Core.Models;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 由程序生成不可交给 LLM 决定的运行硬状态。
    /// </summary>
    public sealed class ProgramHardStateBuilder
    {
        public string Build(ConversationCompressionContext context)
        {
            var safeContext = context ?? ConversationCompressionContext.Default;
            var builder = new StringBuilder();
            builder.AppendLine("[程序硬状态]");
            builder.AppendLine("当前模式：" + safeContext.Mode);
            builder.AppendLine("当前文档：" + (string.IsNullOrWhiteSpace(safeContext.DocumentPath) ? "__active_document__" : safeContext.DocumentPath));
            AppendDocumentState(builder, safeContext.DocumentContext);
            AppendTodoState(builder, safeContext.CurrentTodoBoard);
            AppendWriteRecoveryState(builder, safeContext.PendingWriteStep);
            AppendRecentVerification(builder, safeContext);
            return builder.ToString().Trim();
        }

        private static void AppendDocumentState(StringBuilder builder, DocumentContext document)
        {
            if (document == null)
            {
                builder.AppendLine("文档快照：无。");
                return;
            }

            builder.AppendLine(
                "文档快照：段落="
                + document.ParagraphCount
                + "，表格="
                + document.TableCount
                + "，批注="
                + document.AnnotationCount
                + "，当前段落="
                + document.CursorParagraphIndex);
        }

        private static void AppendTodoState(StringBuilder builder, TodoBoard board)
        {
            if (board == null || board.Items == null || board.Items.Count == 0)
            {
                builder.AppendLine("当前 Todo：无或未启用。");
                return;
            }

            var current = board.Items
                .OrderBy(item => item.Order)
                .FirstOrDefault(item => item.Status == TodoItemStatus.InProgress)
                ?? board.Items
                    .OrderBy(item => item.Order)
                    .FirstOrDefault(item => item.Status == TodoItemStatus.Pending);
            builder.AppendLine("Todo Board：" + board.ExecutionState + "，最近结果=" + board.LastRunOutcome);
            builder.AppendLine("当前 Todo：" + (current == null ? "无待处理项。" : current.Id + " " + Summarize(current.Content, 140)));
        }

        private static void AppendWriteRecoveryState(StringBuilder builder, PendingWriteStepSnapshot pendingWriteStep)
        {
            if (pendingWriteStep == null)
            {
                builder.AppendLine("当前写步骤恢复状态：无待处理写步骤。");
                builder.AppendLine("下一步安全约束：可以继续执行后续步骤。");
                return;
            }

            var state = pendingWriteStep.State ?? string.Empty;
            if (state.IndexOf("RepairRequired", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                builder.AppendLine("当前写步骤恢复状态：待修复，上一写步骤已回滚或未提交。");
            }
            else if (state.IndexOf("AwaitingVerification", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                builder.AppendLine("当前写步骤恢复状态：验证执行中。该状态通常只存在于写入处理流程内部。");
            }
            else
            {
                builder.AppendLine("当前写步骤恢复状态：" + state);
            }

            builder.AppendLine("写步骤工具：" + pendingWriteStep.ToolName);
            builder.AppendLine("写步骤操作：" + Summarize(pendingWriteStep.OperationDescription, 180));
            if (pendingWriteStep.AffectedParagraphs != null && pendingWriteStep.AffectedParagraphs.Length > 0)
            {
                builder.AppendLine("影响段落：" + string.Join(",", pendingWriteStep.AffectedParagraphs.Take(20)));
            }

            builder.AppendLine("修复次数：" + pendingWriteStep.RepairAttempts);
            if (!string.IsNullOrWhiteSpace(pendingWriteStep.LastFailureMessage))
            {
                builder.AppendLine("最近失败：" + Summarize(pendingWriteStep.LastFailureMessage, 260));
            }

            if (!string.IsNullOrWhiteSpace(pendingWriteStep.VerificationFailureReason))
            {
                builder.AppendLine("验证失败原因：" + Summarize(pendingWriteStep.VerificationFailureReason, 220));
            }

            builder.AppendLine("最近回滚结果：如该步骤处于待修复状态，失败写入已通过 Word UndoScope 回滚或未提交。");
            builder.AppendLine("下一步安全约束：不要假设失败修改仍存在；应重新读取目标区域后修复、跳过或停止。");
        }

        private static void AppendRecentVerification(StringBuilder builder, ConversationCompressionContext context)
        {
            var observation = context.RecentInternalObservations == null
                ? null
                : context.RecentInternalObservations
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Content))
                    .LastOrDefault(item =>
                        (item.InternalObservationKind ?? string.Empty).IndexOf("auto_verify", StringComparison.OrdinalIgnoreCase) >= 0
                        || item.Content.IndexOf("自动验证", StringComparison.OrdinalIgnoreCase) >= 0);
            if (observation == null)
            {
                builder.AppendLine("最近自动验证：无。");
                return;
            }

            var content = observation.Content ?? string.Empty;
            if (content.IndexOf("已自动验证通过且已提交", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                builder.AppendLine("最近自动验证：最近写入已验证提交。");
            }
            else if (content.IndexOf("回滚", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("回退", StringComparison.OrdinalIgnoreCase) >= 0
                || content.IndexOf("验证失败", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                builder.AppendLine("最近自动验证：验证失败并已回滚。");
            }
            else
            {
                builder.AppendLine("最近自动验证：" + Summarize(content, 220));
            }
        }

        private static string Summarize(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "无。";
            }

            var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return normalized.Length <= maxChars
                ? normalized
                : normalized.Substring(0, maxChars) + "...";
        }
    }
}
