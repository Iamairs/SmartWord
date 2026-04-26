using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Scripting;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.Scripting;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.OfficeIntegration.Tools
{
    /// <summary>
    /// 执行只读验证脚本，并返回统一的结构化验证结果。
    /// </summary>
    public sealed class VerifyScriptTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = ToolJsonOptions.Default;

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly CSharpScriptExecutor _scriptExecutor;
        private readonly ScriptSecurityValidator _scriptSecurityValidator;
        private readonly JsonElement _inputSchema;

        public VerifyScriptTool(
            WordApplicationWrapper wordApplicationWrapper,
            CSharpScriptExecutor scriptExecutor,
            ScriptSecurityValidator scriptSecurityValidator)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _scriptExecutor = scriptExecutor;
            _scriptSecurityValidator = scriptSecurityValidator;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"description\":{\"type\":\"string\",\"description\":\"本次验证脚本的简要说明。\"},\"code\":{\"type\":\"string\",\"description\":\"只读 C# 验证脚本。应通过 app/doc/WordApp/ActiveDoc 读取 Word COM，并返回结构化结果对象或 JSON 字符串，最少包含 all_passed:boolean 与 results:array。不要声明 Microsoft.Office.Interop.Word 静态类型；访问 Word COM 集合时请使用 Count + 1-based 下标循环，不要使用 foreach。\"}},\"required\":[\"code\"]}")
                .RootElement
                .Clone();
        }

        public string Name => "verify_script";

        public string Description => "执行只读 C# 验证脚本，读取 Word DOM 并返回结构化验证结果。脚本必须返回包含 all_passed 与 results 的对象或 JSON 字符串。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public bool IsVisibleToModel => false;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var request = VerifyScriptRequest.Parse(input);
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                return ToolCallResult.Error(Name, "验证脚本不能为空。");
            }

            var validationResult = _scriptSecurityValidator.Validate(request.Code, ScriptValidationMode.ReadOnly);
            if (!validationResult.IsValid)
            {
                return ToolCallResult.Error(Name, validationResult.Message);
            }

            ScriptExecutionResult executionResult;
            try
            {
                executionResult = await _wordApplicationWrapper
                    .InvokeWithActiveDocumentAsync((wordApplicationObject, documentObject) =>
                    {
                        var globals = new ScriptGlobals
                        {
                            WordApp = wordApplicationObject,
                            ActiveDoc = documentObject,
                            App = wordApplicationObject,
                            Doc = documentObject,
                            app = wordApplicationObject,
                            doc = documentObject,
                            wordApp = wordApplicationObject,
                            activeDoc = documentObject,
                            Context = new ScriptContext()
                        };

                        return _scriptExecutor
                            .ExecuteAsync(request.Code, globals, cancellationToken)
                            .GetAwaiter()
                            .GetResult();
                    })
                    .ConfigureAwait(false);
            }
            catch (CompilationErrorException ex)
            {
                return ToolCallResult.Error(Name, BuildCompilationErrorMessage(ex));
            }
            catch (InvalidCastException ex)
            {
                return ToolCallResult.Error(Name, ExecuteScriptTool.BuildScriptRuntimeErrorMessage(ex));
            }

            if (!TryBuildStructuredOutput(executionResult, out var outputJson, out var errorMessage))
            {
                return ToolCallResult.Error(Name, errorMessage);
            }

            return ToolCallResult.Ok(
                outputJson,
                operationDescription: string.IsNullOrWhiteSpace(request.Description)
                    ? "已完成脚本验证。"
                    : request.Description);
        }

        private static string BuildCompilationErrorMessage(CompilationErrorException exception)
        {
            var diagnostics = exception.Diagnostics == null
                ? string.Empty
                : string.Join(Environment.NewLine, exception.Diagnostics);
            return "验证脚本编译失败。请仅使用动态 COM 读取方式，并返回包含 all_passed 与 results 的结构化结果。"
                + Environment.NewLine
                + diagnostics;
        }

        private static bool TryBuildStructuredOutput(
            ScriptExecutionResult executionResult,
            out string outputJson,
            out string errorMessage)
        {
            outputJson = string.Empty;
            errorMessage = string.Empty;

            var rawPayload = ExtractStructuredPayload(executionResult);
            if (string.IsNullOrWhiteSpace(rawPayload))
            {
                errorMessage = "验证脚本必须返回结构化结果，且至少包含 all_passed 与 results。";
                return false;
            }

            try
            {
                using (var document = JsonDocument.Parse(rawPayload))
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        errorMessage = "验证脚本返回的结果必须是 JSON 对象。";
                        return false;
                    }

                    if (!root.TryGetProperty("all_passed", out var allPassedElement)
                        || (allPassedElement.ValueKind != JsonValueKind.True && allPassedElement.ValueKind != JsonValueKind.False))
                    {
                        errorMessage = "验证脚本返回结果缺少布尔字段 all_passed。";
                        return false;
                    }

                    if (!root.TryGetProperty("results", out var resultsElement)
                        || resultsElement.ValueKind != JsonValueKind.Array)
                    {
                        errorMessage = "验证脚本返回结果缺少数组字段 results。";
                        return false;
                    }

                    outputJson = rawPayload;
                    return true;
                }
            }
            catch (JsonException)
            {
                errorMessage = "验证脚本必须返回可解析的 JSON 对象，且至少包含 all_passed 与 results。";
                return false;
            }
        }

        private static string ExtractStructuredPayload(ScriptExecutionResult executionResult)
        {
            if (executionResult == null)
            {
                return string.Empty;
            }

            if (executionResult.ReturnValue is string returnString)
            {
                return returnString;
            }

            if (executionResult.ReturnValue != null)
            {
                return JsonSerializer.Serialize(executionResult.ReturnValue, executionResult.ReturnValue.GetType(), JsonOptions);
            }

            if (!string.IsNullOrWhiteSpace(executionResult.LogOutput))
            {
                return executionResult.LogOutput;
            }

            return executionResult.Output ?? string.Empty;
        }
    }

    public sealed class VerifyScriptRequest
    {
        public string Description { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;

        public static VerifyScriptRequest Parse(JsonElement input)
        {
            return new VerifyScriptRequest
            {
                Description = ReadString(input, "description"),
                Code = ReadString(input, "code")
            };
        }

        private static string ReadString(JsonElement input, string propertyName)
        {
            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return property.GetString() ?? string.Empty;
        }
    }
}
