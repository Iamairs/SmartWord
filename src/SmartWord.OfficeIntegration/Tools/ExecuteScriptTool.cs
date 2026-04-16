using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// 作为复杂编辑场景的后备路径，执行受控 C# 写入脚本。
    /// </summary>
    public sealed class ExecuteScriptTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly CSharpScriptExecutor _scriptExecutor;
        private readonly ScriptSecurityValidator _scriptSecurityValidator;
        private readonly JsonElement _inputSchema;

        public ExecuteScriptTool(
            WordApplicationWrapper wordApplicationWrapper,
            CSharpScriptExecutor scriptExecutor,
            ScriptSecurityValidator scriptSecurityValidator)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _scriptExecutor = scriptExecutor;
            _scriptSecurityValidator = scriptSecurityValidator;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"description\":{\"type\":\"string\",\"description\":\"本次脚本写入的简要说明。仅当 patch_range 难以表达时再使用脚本。\"},\"write_code\":{\"type\":\"string\",\"description\":\"执行写入的 C# 脚本。当前脚本环境只支持通过 app/doc/WordApp/ActiveDoc 这些 dynamic 全局变量访问 Word COM；不要声明 Paragraph、Range、Shape、InlineShape 等静态 Interop 类型，也不要写 Microsoft.Office.Interop.Word 或 Microsoft.Office.Core.MsoTriState。若直接访问 Word COM 集合（如 Paragraphs、Tables、Comments、Rows、Cells、Shapes、InlineShapes），不要使用 foreach 枚举，应读取 Count 后按 1-based 索引循环访问，例如 for (int i = 1; i <= collection.Count; i++)。如需 Office 常量，优先直接使用已知数值，不要依赖未引用的枚举类型。如需输出调试信息，调用 Write(\\\"...\\\")。\"},\"verify_code\":{\"type\":\"string\",\"description\":\"执行验证的只读 C# 脚本。应通过 app/doc/WordApp/ActiveDoc 读取 Word COM，并返回包含 all_passed:boolean 与 results:array 的结构化结果对象或 JSON 字符串；不要声明静态 Interop 类型，访问 Word COM 集合时请使用 Count + 1-based 下标循环，不要使用 foreach。\"},\"affected_paragraphs\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"若已知会影响哪些段落，可显式填写，便于前端摘要展示。这里的段落索引使用 0-based。\"}},\"required\":[\"write_code\",\"verify_code\"]}")
                .RootElement
                .Clone();
        }

        public string Name => "execute_script";

        public string Description => "执行受控的 C# 写入脚本以完成 patch_range 难以覆盖的复杂写入。输入必须同时提供 write_code 与 verify_code；验证脚本只允许读取 Word DOM，并应返回包含 all_passed 与 results 的结构化结果。";

        public ToolPermission RequiredPermission => ToolPermission.Write;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var request = ExecuteScriptRequest.Parse(input);
            if (string.IsNullOrWhiteSpace(request.WriteCode))
            {
                return ToolCallResult.Error(Name, "write_code 不能为空。");
            }

            if (string.IsNullOrWhiteSpace(request.VerifyCode))
            {
                return ToolCallResult.Error(Name, "verify_code 不能为空。");
            }

            var validationResult = _scriptSecurityValidator.Validate(request.WriteCode, ScriptValidationMode.Write);
            if (!validationResult.IsValid)
            {
                return ToolCallResult.Error(Name, validationResult.Message);
            }

            var verifyValidationResult = _scriptSecurityValidator.Validate(request.VerifyCode, ScriptValidationMode.ReadOnly);
            if (!verifyValidationResult.IsValid)
            {
                return ToolCallResult.Error(Name, "verify_code 无法通过只读校验：" + verifyValidationResult.Message);
            }

            var executionResult = await _wordApplicationWrapper
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

                    try
                    {
                        return _scriptExecutor
                            .ExecuteAsync(request.WriteCode, globals, cancellationToken)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (CompilationErrorException ex)
                    {
                        var diagnostics = ex.Diagnostics == null
                            ? string.Empty
                            : string.Join(
                                Environment.NewLine,
                                ex.Diagnostics.Select(item => item.ToString()));
                        throw new InvalidOperationException(
                            "脚本编译失败。可用全局变量：app、doc、WordApp、ActiveDoc；可调用 Write(\"文本\") 输出调试信息。当前脚本环境只支持 dynamic COM 写法，不支持声明 Microsoft.Office.Interop.Word / Microsoft.Office.Core 的静态类型；请不要写 Paragraph、Range、Shape、InlineShape、MsoTriState 这类类型名。"
                                + Environment.NewLine
                                + diagnostics,
                            ex);
                    }
                    catch (InvalidCastException ex)
                    {
                        throw new InvalidOperationException(
                            BuildScriptRuntimeErrorMessage(ex),
                            ex);
                    }
                })
                .ConfigureAwait(false);

            var payload = new
            {
                success = executionResult.Success,
                output = executionResult.Output,
                log_output = executionResult.LogOutput,
                return_value_type = executionResult.ReturnValueType
            };

            return ToolCallResult.Ok(
                JsonSerializer.Serialize(payload, JsonOptions),
                request.AffectedParagraphs.ToArray(),
                operationDescription: string.IsNullOrWhiteSpace(request.Description)
                    ? "已执行脚本写入。"
                    : request.Description);
        }

        internal static string BuildScriptRuntimeErrorMessage(InvalidCastException exception)
        {
            var rawMessage = exception == null ? string.Empty : exception.Message ?? string.Empty;
            if (rawMessage.IndexOf("IEnumerable", StringComparison.OrdinalIgnoreCase) >= 0
                || rawMessage.IndexOf("DISPID_NEWENUM", StringComparison.OrdinalIgnoreCase) >= 0
                || rawMessage.IndexOf("E_NOINTERFACE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "脚本运行失败。不要对 Word COM 集合使用 foreach；当前 Word COM 集合通常不支持 foreach 枚举，请改为读取 Count 后按 1-based 索引循环访问，例如 for (int i = 1; i <= rows.Count; i++)。涉及 Rows、Cells、Paragraphs、Tables、Shapes、InlineShapes 等集合时都应遵循此规则。原始错误："
                    + rawMessage;
            }

            return "脚本运行失败：" + rawMessage;
        }
    }

    public sealed class ExecuteScriptRequest
    {
        public string Description { get; set; } = string.Empty;

        public string WriteCode { get; set; } = string.Empty;

        public string VerifyCode { get; set; } = string.Empty;

        public List<int> AffectedParagraphs { get; } = new List<int>();

        public static ExecuteScriptRequest Parse(JsonElement input)
        {
            var request = new ExecuteScriptRequest
            {
                Description = ReadString(input, "description"),
                WriteCode = ReadString(input, "write_code"),
                VerifyCode = ReadString(input, "verify_code")
            };

            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty("affected_paragraphs", out var affectedParagraphsElement)
                || affectedParagraphsElement.ValueKind != JsonValueKind.Array)
            {
                return request;
            }

            foreach (var item in affectedParagraphsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var paragraphIndex))
                {
                    request.AffectedParagraphs.Add(paragraphIndex);
                }
            }

            return request;
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
