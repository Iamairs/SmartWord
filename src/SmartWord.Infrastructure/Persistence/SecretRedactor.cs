using System.Text.RegularExpressions;

namespace SmartWord.Infrastructure.Persistence
{
    /// <summary>
    /// 对明显密钥形态做最小脱敏，避免 API Key 误写入本地历史库。
    /// </summary>
    internal static class SecretRedactor
    {
        private static readonly Regex[] Patterns =
        {
            new Regex(@"sk-[A-Za-z0-9_\-]{12,}", RegexOptions.Compiled),
            new Regex(@"Bearer\s+[A-Za-z0-9_\-\.=]{12,}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new Regex(@"(?i)(api[_-]?key\s*[:=]\s*)[^\s"",}]+", RegexOptions.Compiled),
            new Regex(@"(?i)(Authorization\s*:\s*)[^\r\n]+", RegexOptions.Compiled),
            new Regex(@"(?i)(ProtectedApiKey\w*""?\s*[:=]\s*"")[^""]+(""?)", RegexOptions.Compiled),
            new Regex(@"(?i)(ApiKeyDisplay\w*""?\s*[:=]\s*"")[^""]+(""?)", RegexOptions.Compiled)
        };

        public static string Redact(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            var result = value;
            foreach (var pattern in Patterns)
            {
                result = pattern.Replace(result, match =>
                {
                    if (match.Groups.Count >= 3 && match.Groups[1].Success)
                    {
                        return match.Groups[1].Value + "[REDACTED]" + match.Groups[2].Value;
                    }

                    if (match.Groups.Count >= 2 && match.Groups[1].Success)
                    {
                        return match.Groups[1].Value + "[REDACTED]";
                    }

                    return "[REDACTED]";
                });
            }

            return result;
        }
    }
}
