using System.Threading;
using System.Threading.Tasks;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// Plan 模式采访阶段的问答通道：暂停 LLM 循环，等待用户回答后继续。
    /// </summary>
    public interface IQuestionChannel
    {
        bool IsAvailable { get; }

        /// <summary>
        /// 暂停等待用户回答，返回用户选择的选项文本或自由输入内容。
        /// </summary>
        Task<string> WaitForAnswerAsync(string questionId, CancellationToken cancellationToken);
    }
}
