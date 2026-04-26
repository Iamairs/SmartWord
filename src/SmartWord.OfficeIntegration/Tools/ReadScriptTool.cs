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
    /// 执行只读脚本查询，作为复杂 DOM 读取与诊断的万能探针。
    /// </summary>
    public sealed class ReadScriptTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = ToolJsonOptions.Default;

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly CSharpScriptExecutor _scriptExecutor;
        private readonly ScriptSecurityValidator _scriptSecurityValidator;
        private readonly JsonElement _inputSchema;

        public ReadScriptTool(
            WordApplicationWrapper wordApplicationWrapper,
            CSharpScriptExecutor scriptExecutor,
            ScriptSecurityValidator scriptSecurityValidator)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _scriptExecutor = scriptExecutor;
            _scriptSecurityValidator = scriptSecurityValidator;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"description\":{\"type\":\"string\",\"description\":\"本次脚本查询的简要说明。\"},\"code\":{\"type\":\"string\",\"description\":\"只读 C# 查询脚本。应通过 app/doc/WordApp/ActiveDoc 读取 Word COM，并返回任意可序列化结果或字符串；不要声明 Microsoft.Office.Interop.Word 静态类型，访问 Word COM 集合时请使用 Count + 1-based 下标循环，不要使用 foreach。\"}},\"required\":[\"code\"]}")
                .RootElement
                .Clone();
        }

        public string Name => "read_script";

        public string Description => "执行只读 C# 查询脚本，读取任意 Word DOM 结构并返回结果。适合复杂诊断、万能查找与格式探针。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public bool IsVisibleToModel => true;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var request = ReadScriptRequest.Parse(input);
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                return ToolCallResult.Error(Name, "查询脚本不能为空。");
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

            var payload = new
            {
                output = ExtractReadablePayload(executionResult),
                log_output = executionResult == null ? string.Empty : executionResult.LogOutput,
                return_value_type = executionResult == null ? string.Empty : executionResult.ReturnValueType
            };

            return ToolCallResult.Ok(
                JsonSerializer.Serialize(payload, JsonOptions),
                operationDescription: string.IsNullOrWhiteSpace(request.Description)
                    ? "已完成脚本查询。"
                    : request.Description);
        }

        private static string BuildCompilationErrorMessage(CompilationErrorException exception)
        {
            var diagnostics = exception.Diagnostics == null
                ? string.Empty
                : string.Join(Environment.NewLine, exception.Diagnostics);
            return "查询脚本编译失败。请仅使用动态 COM 读取方式，不要声明 Microsoft.Office.Interop.Word 静态类型。"
                + Environment.NewLine
                + diagnostics;
        }

        private static string ExtractReadablePayload(ScriptExecutionResult executionResult)
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

    public sealed class ReadScriptRequest
    {
        public string Description { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;

        public static ReadScriptRequest Parse(JsonElement input)
        {
            return new ReadScriptRequest
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
