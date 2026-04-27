using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Infrastructure.Skills
{
    /// <summary>
    /// 使用本地 JSON 文件保存用户记住的 Skill 脚本授权。
    /// </summary>
    public sealed class FileSkillScriptApprovalStore : ISkillScriptApprovalStore
    {
        private readonly string _approvalPath;

        public FileSkillScriptApprovalStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SmartWord",
                "skills",
                "skill-script-approvals.json"))
        {
        }

        public FileSkillScriptApprovalStore(string approvalPath)
        {
            _approvalPath = Path.GetFullPath(approvalPath ?? string.Empty);
        }

        public Task<bool> IsApprovedAsync(SkillScriptApprovalKey key, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (key == null)
                {
                    return false;
                }

                var state = LoadState();
                return state.Approvals.ContainsKey(key.ToStableKey());
            }, cancellationToken);
        }

        public Task ApproveAsync(
            SkillScriptApprovalKey key,
            string purpose,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (key == null)
                {
                    throw new ArgumentNullException(nameof(key));
                }

                var state = LoadState();
                state.Approvals[key.ToStableKey()] = new SkillScriptApprovalRecord
                {
                    Key = key,
                    Purpose = purpose ?? string.Empty,
                    ApprovedAtUtc = DateTimeOffset.UtcNow
                };
                SaveState(state);
            }, cancellationToken);
        }

        public Task RevokeAsync(SkillScriptApprovalKey key, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (key == null)
                {
                    return;
                }

                var state = LoadState();
                state.Approvals.Remove(key.ToStableKey());
                SaveState(state);
            }, cancellationToken);
        }

        public Task<IReadOnlyList<SkillScriptApprovalRecord>> GetApprovalsAsync(CancellationToken cancellationToken)
        {
            return Task.Run<IReadOnlyList<SkillScriptApprovalRecord>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return LoadState()
                    .Approvals
                    .Values
                    .OrderByDescending(item => item.ApprovedAtUtc)
                    .ToList();
            }, cancellationToken);
        }

        private ApprovalState LoadState()
        {
            try
            {
                if (!File.Exists(_approvalPath))
                {
                    return new ApprovalState();
                }

                return JsonConvert.DeserializeObject<ApprovalState>(
                        File.ReadAllText(_approvalPath, Encoding.UTF8))
                    ?? new ApprovalState();
            }
            catch
            {
                return new ApprovalState();
            }
        }

        private void SaveState(ApprovalState state)
        {
            var directory = Path.GetDirectoryName(_approvalPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                _approvalPath,
                JsonConvert.SerializeObject(state ?? new ApprovalState(), Formatting.Indented),
                Encoding.UTF8);
        }

        private sealed class ApprovalState
        {
            public Dictionary<string, SkillScriptApprovalRecord> Approvals { get; set; } =
                new Dictionary<string, SkillScriptApprovalRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
