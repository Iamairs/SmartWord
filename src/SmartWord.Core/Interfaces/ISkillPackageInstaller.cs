using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 提供外部 Skill 包的预览与确认安装能力。预览阶段不得执行包内脚本。
    /// </summary>
    public interface ISkillPackageInstaller
    {
        Task<SkillImportPreview> PreviewNetworkAsync(
            string sourceUrl,
            CancellationToken cancellationToken);

        Task<SkillImportPreview> PreviewFoldersAsync(
            IReadOnlyList<string> folderPaths,
            CancellationToken cancellationToken);

        Task<SkillImportResult> InstallAsync(
            SkillImportInstallRequest request,
            CancellationToken cancellationToken);

        Task CancelPreviewAsync(string sessionId, CancellationToken cancellationToken);
    }
}
