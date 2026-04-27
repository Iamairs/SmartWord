using System;
using System.IO;
using System.Text.RegularExpressions;

namespace SmartWord.Infrastructure.Skills
{
    /// <summary>
    /// Skill 文件系统路径守卫，统一处理名称白名单和根目录越界检查。
    /// </summary>
    public static class SkillPathGuard
    {
        private static readonly Regex NamePattern =
            new Regex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

        public static bool IsValidSkillName(string name)
        {
            return !string.IsNullOrWhiteSpace(name)
                && NamePattern.IsMatch(name.Trim());
        }

        public static string NormalizeSkillName(string name)
        {
            return (name ?? string.Empty).Trim().ToLowerInvariant();
        }

        public static void EnsureValidSkillName(string name)
        {
            if (!IsValidSkillName(name))
            {
                throw new ArgumentException("Skill 名称只能包含小写字母、数字和连字符，且长度不能超过 64。", nameof(name));
            }
        }

        public static string CombineSkillRoot(string root, string skillName)
        {
            EnsureValidSkillName(skillName);
            var safeRoot = Path.GetFullPath(root ?? string.Empty);
            var target = Path.GetFullPath(Path.Combine(safeRoot, NormalizeSkillName(skillName)));
            EnsureInsideRoot(safeRoot, target);
            return target;
        }

        public static void EnsureInsideRoot(string root, string targetPath)
        {
            var safeRoot = EnsureTrailingSeparator(Path.GetFullPath(root ?? string.Empty));
            var safeTarget = Path.GetFullPath(targetPath ?? string.Empty);
            if (!safeTarget.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Skill 路径越界，操作已拒绝。");
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }
    }
}
