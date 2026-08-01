using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmartWord.Core.Enums;
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
                    || !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase),
                TrustLevel = ParseTrustLevel(values.TryGetValue("trust_level", out var trustLevel)
                    ? trustLevel
                    : string.Empty),
                Source = values.TryGetValue("source", out var source) ? source : string.Empty,
                ActivationTriggers = ReadList(values, "activation.triggers", "activation_triggers"),
                ActivationExcludedTriggers = ReadList(
                    values,
                    "activation.excluded_triggers",
                    "activation_excluded_triggers"),
                SupportedModes = ReadList(values, "supported_modes"),
                RequiredTools = ReadList(values, "required_tools")
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
                var currentSection = string.Empty;
                var currentListKey = string.Empty;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (string.Equals(trimmed, "---", StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(currentListKey))
                    {
                        AppendListValue(values, currentListKey, trimmed.Substring(2).Trim().Trim('"'));
                        continue;
                    }

                    var separatorIndex = trimmed.IndexOf(':');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var key = trimmed.Substring(0, separatorIndex).Trim();
                    var value = trimmed.Substring(separatorIndex + 1).Trim().Trim('"');
                    var indent = line.Length - line.TrimStart().Length;
                    if (indent == 0 && string.IsNullOrWhiteSpace(value))
                    {
                        currentSection = key;
                        currentListKey = string.Empty;
                        continue;
                    }

                    var fullKey = indent > 0 && !string.IsNullOrWhiteSpace(currentSection)
                        ? currentSection + "." + key
                        : key;
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            currentListKey = fullKey;
                        }
                        else
                        {
                            values[fullKey] = value;
                            currentListKey = string.Empty;
                        }
                    }
                }
            }

            return values;
        }

        private static SkillTrustLevel ParseTrustLevel(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "built_in":
                case "builtin":
                    return SkillTrustLevel.BuiltIn;
                case "external":
                    return SkillTrustLevel.External;
                case "user":
                default:
                    return SkillTrustLevel.User;
            }
        }

        private static IReadOnlyList<string> ReadList(
            IReadOnlyDictionary<string, string> values,
            params string[] keys)
        {
            foreach (var key in keys ?? new string[0])
            {
                if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                return raw
                    .Split(new[] { '\u001f', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return new List<string>();
        }

        private static void AppendListValue(IDictionary<string, string> values, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            values.TryGetValue(key, out var existing);
            values[key] = string.IsNullOrWhiteSpace(existing)
                ? value
                : existing + '\u001f' + value;
        }
    }
}
