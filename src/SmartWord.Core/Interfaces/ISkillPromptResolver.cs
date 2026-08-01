using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 根据用户显式选择和消息内容生成 Skill prompt 上下文。
    /// </summary>
    public interface ISkillPromptResolver
    {
        Task<SkillPromptContext> ResolveAsync(
            string userMessage,
            IEnumerable<string> selectedSkillNames,
            AgentMode mode,
            CancellationToken cancellationToken);

        Task<SkillPromptContext> ResolveAsync(
            string userMessage,
            IEnumerable<string> selectedSkillNames,
            AgentRunOptions options,
            CancellationToken cancellationToken);
    }
}
