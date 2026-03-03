using System;
using System.Text.RegularExpressions;

namespace SmartWord.Services.Vba
{
    public sealed class VbaCodeSanitizer
    {
        public string SanitizeAndValidate(string rawCode, string entryPoint)
        {
            if (string.IsNullOrWhiteSpace(rawCode))
            {
                throw new ArgumentException("VBA code is empty.", nameof(rawCode));
            }

            string sanitized = rawCode.Trim();
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
