using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 执行已解析的 Skill 脚本。实现层必须创建隔离 workspace 并收敛脚本能力。
    /// </summary>
    public interface ISkillScriptRunner
    {
        Task<SkillScriptRunResult> RunAsync(
            SkillScriptRunRequest request,
            CancellationToken cancellationToken);
    }
}
