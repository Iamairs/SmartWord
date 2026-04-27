using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 管理本地 Skill 能力包。实现层负责路径安全、模板创建和启停状态。
    /// </summary>
    public interface ISkillStore
    {
        Task<IReadOnlyList<SkillDefinition>> GetSkillsAsync(CancellationToken cancellationToken);

        Task<SkillDetail> GetSkillDetailAsync(string name, CancellationToken cancellationToken);

        Task<SkillDetail> CreateSkillAsync(CreateSkillRequest request, CancellationToken cancellationToken);

        Task<SkillDetail> SaveSkillAsync(SaveSkillRequest request, CancellationToken cancellationToken);

        Task DeleteSkillAsync(string name, CancellationToken cancellationToken);

        Task SetSkillEnabledAsync(string name, bool enabled, CancellationToken cancellationToken);
    }
}
