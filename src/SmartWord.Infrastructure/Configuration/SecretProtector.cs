using System;
using System.Security.Cryptography;
using System.Text;

namespace SmartWord.Infrastructure.Configuration
{
    /// <summary>
    /// 使用 Windows DPAPI 保护本机当前用户范围内的敏感配置。
    /// </summary>
    public static class SecretProtector
    {
        private const string Prefix = "dpapi:v1:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SmartWord.Settings.Secret.v1");

        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return string.Empty;
            }

            var rawBytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(rawBytes, Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }

        public static string Unprotect(string protectedText)
        {
            if (string.IsNullOrWhiteSpace(protectedText))
            {
                return string.Empty;
            }

            if (!IsProtectedValue(protectedText))
            {
                return protectedText;
            }

            var payload = protectedText.Substring(Prefix.Length);
            var protectedBytes = Convert.FromBase64String(payload);
            var rawBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(rawBytes);
        }

        public static bool IsProtectedValue(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static bool HasSecret(string plainText, string protectedText)
        {
            return !string.IsNullOrWhiteSpace(plainText)
                || !string.IsNullOrWhiteSpace(protectedText);
        }
    }
}
