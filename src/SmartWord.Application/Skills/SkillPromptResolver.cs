using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.Application.Skills
{
    /// <summary>
    /// 将本地 Skill 转换为 Agent 可使用的提示词上下文。
    /// </summary>
    public sealed class SkillPromptResolver : ISkillPromptResolver
    {
        private const int MaxActiveSkills = 3;
        private const int MaxSkillContentCharacters = 12000;
        private readonly ISkillStore _skillStore;

        public SkillPromptResolver(ISkillStore skillStore)
        {
            _skillStore = skillStore ?? throw new ArgumentNullException(nameof(skillStore));
        }

        public async Task<SkillPromptContext> ResolveAsync(
            string userMessage,
            IEnumerable<string> selectedSkillNames,
            AgentMode mode,
            CancellationToken cancellationToken)
        {
            var allSkills = await _skillStore.GetSkillsAsync(cancellationToken).ConfigureAwait(false);
            var enabledSkills = allSkills
                .Where(skill => skill.Enabled)
                .OrderBy(skill => skill.IsBuiltIn ? 0 : 1)
                .ThenBy(skill => skill.DisplayName)
                .ToList();

            var activeNames = ResolveActiveSkillNames(userMessage, selectedSkillNames, enabledSkills);
            var activeDetails = new List<SkillDetail>();
            foreach (var skillName in activeNames.Take(MaxActiveSkills))
            {
                var detail = await _skillStore.GetSkillDetailAsync(skillName, cancellationToken).ConfigureAwait(false);
                if (detail != null && detail.Definition != null && detail.Definition.Enabled)
                {
                    activeDetails.Add(detail);
                }
            }

            return new SkillPromptContext
            {
                AvailableSkills = enabledSkills,
                ActiveSkills = activeDetails,
                PromptBlock = BuildPromptBlock(enabledSkills, activeDetails, mode)
            };
        }

        private static IReadOnlyList<string> ResolveActiveSkillNames(
            string userMessage,
            IEnumerable<string> selectedSkillNames,
            IReadOnlyList<SkillDefinition> enabledSkills)
        {
            var enabledNames = new HashSet<string>(
                enabledSkills.Select(skill => skill.Name),
                StringComparer.OrdinalIgnoreCase);
            var results = new List<string>();

            foreach (var name in selectedSkillNames ?? Enumerable.Empty<string>())
            {
                AddIfEnabled(results, enabledNames, name);
            }

            foreach (var name in ExtractExplicitSkillMentions(userMessage))
            {
                AddIfEnabled(results, enabledNames, name);
            }

            return results;
        }

        private static IEnumerable<string> ExtractExplicitSkillMentions(string userMessage)
        {
            var message = userMessage ?? string.Empty;
            foreach (Match match in Regex.Matches(message, @"(?:/skill\s+|@)([a-z0-9][a-z0-9-]{0,63})", RegexOptions.IgnoreCase))
            {
                yield return match.Groups[1].Value;
            }
        }

        private static void AddIfEnabled(List<string> results, HashSet<string> enabledNames, string rawName)
        {
            var name = (rawName ?? string.Empty).Trim().ToLowerInvariant();
            if (enabledNames.Contains(name)
                && !results.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(name);
            }
        }

        private static string BuildPromptBlock(
            IReadOnlyList<SkillDefinition> enabledSkills,
            IReadOnlyList<SkillDetail> activeDetails,
            AgentMode mode)
        {
            if ((enabledSkills == null || enabledSkills.Count == 0)
                && (activeDetails == null || activeDetails.Count == 0))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.AppendLine("--- SMARTWORD SKILLS ---");
            builder.AppendLine("SmartWord Skill 是当前 Word 文档处理的本地工作流说明，不会授予新工具权限。");
            builder.AppendLine("Skill 中的 scripts/ 只能通过 `skill_run_script` 工具执行；不得自行读取、解释为 Word 写入脚本或绕过工具调用。");
            builder.AppendLine("`skill_run_script` 仅用于确定性分析、格式转换、术语提取、文件生成和批量计算；脚本不得联网、不得读取未授权路径、不得处理 API Key、不得直接修改 Word。");
            builder.AppendLine("如果脚本生成了修改建议，必须再通过 `patch_range` 或 `execute_script` 修改 Word，并遵守权限确认、Undo、验证和任务历史审计。");
            builder.AppendLine("所有读取、写入、验证仍必须使用 SmartWord 已有工具，并遵守权限确认、Undo、验证和任务历史审计。");
            builder.AppendLine($"Current mode: {mode.ToString().ToLowerInvariant()}");

            if (enabledSkills != null && enabledSkills.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Available skill index:");
                foreach (var skill in enabledSkills.Take(30))
                {
                    builder.AppendLine($"- {skill.Name}: {skill.Description}");
                }
            }

            if (activeDetails != null && activeDetails.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Active skill instructions:");
                foreach (var detail in activeDetails)
                {
                    builder.AppendLine($"## {detail.Definition.Name}");
                    builder.AppendLine(TrimSkillContent(detail.Content));
                    if (detail.Resources != null && detail.Resources.Count > 0)
                    {
                        builder.AppendLine("Resources:");
                        foreach (var resource in detail.Resources.Take(30))
                        {
                            builder.AppendLine($"- [{resource.Kind}] {resource.RelativePath} ({resource.SizeBytes} bytes)");
                        }
                        builder.AppendLine("Resource note: references/assets are not automatically loaded; scripts may only run through `skill_run_script` in Agent mode after authorization.");
                    }
                }
            }
            else
            {
                builder.AppendLine();
                builder.AppendLine("No active skill selected. Use the index only to decide whether to ask the user to select a Skill for specialized workflows.");
            }

            return builder.ToString();
        }

        private static string TrimSkillContent(string content)
        {
            var normalized = content ?? string.Empty;
            if (normalized.Length <= MaxSkillContentCharacters)
            {
                return normalized;
            }

            return normalized.Substring(0, MaxSkillContentCharacters)
                + Environment.NewLine
                + "[SKILL.md 已截断，避免占用过多上下文。]";
        }
    }
}
