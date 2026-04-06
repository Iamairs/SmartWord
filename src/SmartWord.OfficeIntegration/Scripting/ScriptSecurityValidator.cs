using System.Collections.Generic;

namespace SmartWord.OfficeIntegration.Scripting
{
    /// <summary>
    /// 预留脚本安全校验入口。
    /// </summary>
    public class ScriptSecurityValidator
    {
        private static readonly HashSet<string> ForbiddenPrefixes = new HashSet<string>
        {
            "System.IO",
            "System.Net",
            "System.Diagnostics"
        };

        public ValidationResult Validate(string code)
        {
            foreach (var prefix in ForbiddenPrefixes)
            {
                if (!string.IsNullOrWhiteSpace(code) && code.Contains(prefix))
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        Message = "检测到受限命名空间：" + prefix
                    };
                }
            }

            return new ValidationResult
            {
                IsValid = true,
                Message = "ok"
            };
        }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }

        public string Message { get; set; } = string.Empty;
    }
}
