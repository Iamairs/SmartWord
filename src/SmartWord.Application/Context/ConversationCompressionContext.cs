using System.Collections.Generic;
using SmartWord.Core.Enums;
using SmartWord.Core.Models;

namespace SmartWord.Application.Context
{
    /// <summary>
    /// 压缩器所需的运行快照。只承载状态，不访问 Todo 存储或 Office 对象。
    /// </summary>
    public sealed class ConversationCompressionContext
    {
        public AgentMode Mode { get; set; } = AgentMode.Ask;

        public string DocumentPath { get; set; } = string.Empty;

        public string CurrentUserGoal { get; set; } = string.Empty;

        public TodoBoard CurrentTodoBoard { get; set; }

        public ExecutionPlan ActivePlan { get; set; }

        public PendingWriteStepSnapshot PendingWriteStep { get; set; }

        public DocumentContext DocumentContext { get; set; }

        public IList<AgentMessage> RecentInternalObservations { get; set; } = new List<AgentMessage>();

        public static ConversationCompressionContext Default { get; } = new ConversationCompressionContext();
    }

    /// <summary>
    /// 当前写步骤的压缩快照，避免压缩器依赖编排器内部类型。
    /// </summary>
    public sealed class PendingWriteStepSnapshot
    {
        public string ToolCallId { get; set; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public int[] AffectedParagraphs { get; set; }

        public string OperationDescription { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public int RepairAttempts { get; set; }

        public string LastFailureMessage { get; set; } = string.Empty;

        public string VerificationToolName { get; set; } = string.Empty;

        public string VerificationOperationDescription { get; set; } = string.Empty;

        public string VerificationFailureReason { get; set; } = string.Empty;
    }
}
