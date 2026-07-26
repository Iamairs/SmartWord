using System.Collections.Generic;
using SmartWord.Core.Models;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// 保存跨模型迭代共享的轻量运行状态，避免主循环散落可变计数和引用表。
    /// </summary>
    internal sealed class AgentRunState
    {
        internal readonly Dictionary<int, CitationEntry> CitationRegistry =
            new Dictionary<int, CitationEntry>();

        internal readonly Dictionary<int, int> ParagraphToRef =
            new Dictionary<int, int>();

        internal int NextCitationRef = 1;

        internal int ConsecutiveFailures;

        internal string LastFailureSummary = string.Empty;

        internal int InterviewRound;

        internal AgentMessage FinalAssistantMessage;
    }
}
