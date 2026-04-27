using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 管理用户记住的 Skill 脚本授权。
    /// </summary>
    public interface ISkillScriptApprovalStore
    {
        Task<bool> IsApprovedAsync(SkillScriptApprovalKey key, CancellationToken cancellationToken);

        Task ApproveAsync(
            SkillScriptApprovalKey key,
            string purpose,
            CancellationToken cancellationToken);

        Task RevokeAsync(SkillScriptApprovalKey key, CancellationToken cancellationToken);

        Task<IReadOnlyList<SkillScriptApprovalRecord>> GetApprovalsAsync(CancellationToken cancellationToken);
    }
}
