using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Application.Skills;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using Xunit;

namespace SmartWord.Application.Tests.Skills
{
    public sealed class SkillPromptResolverTests
    {
        [Fact]
        public async Task ResolveAsync_SelectedSkill_IncludesActiveSkillContent()
        {
            var resolver = new SkillPromptResolver(new FakeSkillStore());

            var context = await resolver.ResolveAsync(
                "帮我交付前检查",
                new[] { "document-finalizer" },
                AgentMode.Agent,
                CancellationToken.None);

            Assert.Contains("Active skill instructions", context.PromptBlock);
            Assert.Contains("检查占位符和编号", context.PromptBlock);
        }

        [Fact]
        public async Task ResolveAsync_ExplicitMention_LoadsSkill()
        {
            var resolver = new SkillPromptResolver(new FakeSkillStore());

            var context = await resolver.ResolveAsync(
                "/skill contract-review 帮我审合同",
                Enumerable.Empty<string>(),
                AgentMode.Plan,
                CancellationToken.None);

            Assert.Contains("contract-review", context.PromptBlock);
            Assert.Contains("审查付款和违约风险", context.PromptBlock);
        }

        [Fact]
        public async Task ResolveAsync_NoActiveSkill_OnlyIncludesIndexAndSafetyNotice()
        {
            var resolver = new SkillPromptResolver(new FakeSkillStore());

            var context = await resolver.ResolveAsync(
                "总结当前文档",
                Enumerable.Empty<string>(),
                AgentMode.Ask,
                CancellationToken.None);

            Assert.Contains("Available skill index", context.PromptBlock);
            Assert.Contains("只能通过 `skill_run_script` 工具执行", context.PromptBlock);
            Assert.Contains("No active skill selected", context.PromptBlock);
            Assert.DoesNotContain("检查占位符和编号", context.PromptBlock);
        }

        [Fact]
        public async Task ResolveAsync_DisabledSkill_DoesNotLoadContent()
        {
            var resolver = new SkillPromptResolver(new FakeSkillStore());

            var context = await resolver.ResolveAsync(
                "@disabled-skill 执行",
                new[] { "disabled-skill" },
                AgentMode.Agent,
                CancellationToken.None);

            Assert.DoesNotContain("disabled body", context.PromptBlock);
        }

        private sealed class FakeSkillStore : ISkillStore
        {
            private readonly List<SkillDetail> _details = new List<SkillDetail>
            {
                new SkillDetail
                {
                    Definition = new SkillDefinition
                    {
                        Name = "document-finalizer",
                        Description = "交付前检查 Word 文档。",
                        DisplayName = "文档终检",
                        Enabled = true
                    },
                    Content = "检查占位符和编号。",
                    Resources = new List<SkillResource>
                    {
                        new SkillResource { Kind = "scripts", RelativePath = "scripts/check.py", SizeBytes = 10 }
                    }
                },
                new SkillDetail
                {
                    Definition = new SkillDefinition
                    {
                        Name = "contract-review",
                        Description = "审查合同风险。",
                        DisplayName = "合同审阅",
                        Enabled = true
                    },
                    Content = "审查付款和违约风险。"
                },
                new SkillDetail
                {
                    Definition = new SkillDefinition
                    {
                        Name = "disabled-skill",
                        Description = "禁用 Skill。",
                        DisplayName = "禁用",
                        Enabled = false
                    },
                    Content = "disabled body"
                }
            };

            public Task<IReadOnlyList<SkillDefinition>> GetSkillsAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult<IReadOnlyList<SkillDefinition>>(_details.Select(detail => detail.Definition).ToList());
            }

            public Task<SkillDetail> GetSkillDetailAsync(string name, CancellationToken cancellationToken)
            {
                return Task.FromResult(_details.FirstOrDefault(detail => detail.Definition.Name == name));
            }

            public Task<SkillDetail> CreateSkillAsync(CreateSkillRequest request, CancellationToken cancellationToken)
            {
                throw new System.NotImplementedException();
            }

            public Task<SkillDetail> SaveSkillAsync(SaveSkillRequest request, CancellationToken cancellationToken)
            {
                throw new System.NotImplementedException();
            }

            public Task DeleteSkillAsync(string name, CancellationToken cancellationToken)
            {
                throw new System.NotImplementedException();
            }

            public Task SetSkillEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
            {
                throw new System.NotImplementedException();
            }

            public Task<IReadOnlyList<SkillScriptInfo>> GetSkillScriptsAsync(string name, CancellationToken cancellationToken)
            {
                var detail = _details.FirstOrDefault(item => item.Definition.Name == name);
                return Task.FromResult<IReadOnlyList<SkillScriptInfo>>(
                    detail == null
                        ? new List<SkillScriptInfo>()
                        : detail.Scripts);
            }

            public Task<SkillScriptResolution> ResolveScriptAsync(
                string skillName,
                string scriptPath,
                string runtime,
                CancellationToken cancellationToken)
            {
                throw new System.NotImplementedException();
            }
        }
    }
}
