using System;
using System.Text.RegularExpressions;

// 文件说明：
// VBA 代码净化与校验组件，负责去除包裹标记并验证过程结构与入口存在性。
namespace SmartWord.Services.Vba
{
    /// <summary>
    /// VBA 代码净化器。
    /// </summary>
    public sealed class VbaCodeSanitizer
    {
        /// <summary>
        /// 净化并校验 VBA 代码。
        /// </summary>
        /// <param name="rawCode">模型返回的原始代码。</param>
        /// <param name="entryPoint">期望入口过程名。</param>
        /// <returns>净化后的 VBA 代码。</returns>
        /// <exception cref="ArgumentException">输入代码为空时抛出。</exception>
        /// <exception cref="InvalidOperationException">结构或入口不合法时抛出。</exception>
        public string SanitizeAndValidate(string rawCode, string entryPoint)
        {
            if (string.IsNullOrWhiteSpace(rawCode))
            {
                throw new ArgumentException("VBA code is empty.", nameof(rawCode));
            }

            string sanitized = rawCode.Trim();
            // 去除 markdown 代码块包裹，避免直接注入失败。
            sanitized = Regex.Replace(sanitized, "^```[a-zA-Z]*\\s*", string.Empty);
            sanitized = Regex.Replace(sanitized, "\\s*```$", string.Empty);

            if (sanitized.IndexOf("Sub ", StringComparison.OrdinalIgnoreCase) < 0 ||
                sanitized.IndexOf("End Sub", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("Generated content is not a valid VBA procedure.");
            }

            if (!string.IsNullOrWhiteSpace(entryPoint) &&
                sanitized.IndexOf("Sub " + entryPoint, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("VBA entry point not found: " + entryPoint);
            }

            return sanitized;
        }
    }
}
