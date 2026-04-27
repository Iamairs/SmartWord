using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.Skills;
using Xunit;

namespace SmartWord.Application.Tests.Infrastructure
{
    public sealed class FileSkillScriptApprovalStoreTests
    {
        [Fact]
        public async Task IsApprovedAsync_SameHashAndPermissionSet_ReturnsTrue()
        {
            var path = Path.Combine(Path.GetTempPath(), "smartword-approvals-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var store = new FileSkillScriptApprovalStore(path);
                var key = CreateKey("hash-1", "perm-1");

                await store.ApproveAsync(key, "测试授权", CancellationToken.None);

                Assert.True(await store.IsApprovedAsync(key, CancellationToken.None));
                Assert.False(await store.IsApprovedAsync(CreateKey("hash-2", "perm-1"), CancellationToken.None));
                Assert.False(await store.IsApprovedAsync(CreateKey("hash-1", "perm-2"), CancellationToken.None));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static SkillScriptApprovalKey CreateKey(string hash, string permissionSet)
        {
            return new SkillScriptApprovalKey
            {
                SkillName = "term-check",
                RelativeScriptPath = "scripts/scan.py",
                ScriptHash = hash,
                Runtime = "python",
                PermissionSet = permissionSet
            };
        }
    }
}
