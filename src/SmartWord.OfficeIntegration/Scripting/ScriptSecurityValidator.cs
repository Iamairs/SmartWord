using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SmartWord.OfficeIntegration.Scripting
{
    /// <summary>
    /// 对脚本做最小可用的静态分析，拦截高风险命名空间与符号。
    /// </summary>
    public class ScriptSecurityValidator
    {
        private static readonly string[] ForbiddenNamespacePrefixes =
        {
            "System.IO",
            "System.Net",
            "System.Diagnostics",
            "System.Reflection",
            "Microsoft.Win32"
        };

        private static readonly HashSet<string> ForbiddenIdentifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "File",
            "Directory",
            "Path",
            "Process",
            "HttpClient",
            "WebClient",
            "Registry",
            "Assembly",
            "Activator",
            "AppDomain"
        };

        public ValidationResult Validate(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return ValidationResult.Invalid("脚本内容不能为空。");
            }

            var syntaxTree = CSharpSyntaxTree.ParseText(code);
            var diagnostics = syntaxTree.GetDiagnostics()
                .Where(item => item.Severity == DiagnosticSeverity.Error)
                .ToList();
            if (diagnostics.Count > 0)
            {
                return ValidationResult.Invalid("脚本语法无效：" + diagnostics[0].GetMessage());
            }

            var root = syntaxTree.GetRoot();
            foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                var namespaceText = usingDirective.Name?.ToString() ?? string.Empty;
                if (MatchesForbiddenNamespace(namespaceText))
                {
                    return ValidationResult.Invalid("检测到受限 using 命名空间：" + namespaceText);
                }
            }

            foreach (var qualifiedName in root.DescendantNodes().OfType<QualifiedNameSyntax>())
            {
                var text = qualifiedName.ToString();
                if (MatchesForbiddenNamespace(text))
                {
                    return ValidationResult.Invalid("检测到受限命名空间访问：" + text);
                }
            }

            foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var text = memberAccess.ToString();
                if (ForbiddenNamespacePrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    return ValidationResult.Invalid("检测到受限成员访问：" + text);
                }
            }

            foreach (var identifierName in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var identifier = identifierName.Identifier.ValueText;
                if (ForbiddenIdentifiers.Contains(identifier))
                {
                    return ValidationResult.Invalid("检测到受限标识符：" + identifier);
                }
            }

            return ValidationResult.Valid();
        }

        private static bool MatchesForbiddenNamespace(string namespaceText)
        {
            return ForbiddenNamespacePrefixes.Any(prefix =>
                namespaceText.StartsWith(prefix, StringComparison.Ordinal));
        }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }

        public string Message { get; set; } = string.Empty;

        public static ValidationResult Valid()
        {
            return new ValidationResult
            {
                IsValid = true,
                Message = "ok"
            };
        }

        public static ValidationResult Invalid(string message)
        {
            return new ValidationResult
            {
                IsValid = false,
                Message = message ?? string.Empty
            };
        }
    }
}
