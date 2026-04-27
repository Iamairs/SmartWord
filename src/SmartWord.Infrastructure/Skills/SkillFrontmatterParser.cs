using System;
using System.Collections.Generic;
using System.IO;
using SmartWord.Core.Models;

namespace SmartWord.Infrastructure.Skills
{
    /// <summary>
    /// 解析 SKILL.md 的简化 YAML frontmatter。仅支持首版需要的 key/value 字段。
    /// </summary>
    public static class SkillFrontmatterParser
    {
        public static SkillDefinition Parse(string content, string fallbackName)
        {
            var values = ParseFrontmatter(content ?? string.Empty);
            var normalizedFallback = SkillPathGuard.NormalizeSkillName(fallbackName);
            var name = values.TryGetValue("name", out var rawName)
                ? SkillPathGuard.NormalizeSkillName(rawName)
                : normalizedFallback;

            if (!SkillPathGuard.IsValidSkillName(name))
            {
                name = normalizedFallback;
            }

            return new SkillDefinition
            {
                Name = name,
                DisplayName = values.TryGetValue("display_name", out var displayName)
                    ? displayName
                    : name,
                Description = values.TryGetValue("description", out var description)
                    ? description
                    : string.Empty,
                Version = values.TryGetValue("version", out var version)
                    ? version
                    : string.Empty,
                Enabled = !values.TryGetValue("enabled", out var enabled)
                    || !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase)
            };
        }

        public static string ReadFrontmatterName(string content)
        {
            var values = ParseFrontmatter(content ?? string.Empty);
            return values.TryGetValue("name", out var name)
                ? SkillPathGuard.NormalizeSkillName(name)
                : string.Empty;
        }

        private static Dictionary<string, string> ParseFrontmatter(string content)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = new StringReader(content ?? string.Empty))
            {
                var firstLine = reader.ReadLine();
                if (!string.Equals((firstLine ?? string.Empty).Trim(), "---", StringComparison.Ordinal))
                {
                    return values;
                }

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (string.Equals(trimmed, "---", StringComparison.Ordinal))
                    {
                        break;
                    }

                    var separatorIndex = trimmed.IndexOf(':');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var key = trimmed.Substring(0, separatorIndex).Trim();
                    var value = trimmed.Substring(separatorIndex + 1).Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        values[key] = value;
                    }
                }
            }

            return values;
        }
    }
}
