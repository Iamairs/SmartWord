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
    /// 作为复杂编辑场景的后备路径，执行受控 C# 脚本。
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
                "{\"type\":\"object\",\"properties\":{\"description\":{\"type\":\"string\",\"description\":\"本次脚本写入的简要说明。\"},\"code\":{\"type\":\"string\",\"description\":\"C# 脚本。可直接使用 app/doc/WordApp/ActiveDoc 访问 Word 应用与当前文档；可调用 Write(\\\"...\\\") 输出调试信息。\"},\"affected_paragraphs\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"description\":\"若已知会影响哪些段落，可显式填写，便于前端摘要展示。\"}},\"required\":[\"code\"]}")
                .RootElement
                .Clone();
        }

        public string Name => "execute_script";

        public string Description => "执行受控的 C# 脚本以完成 patch_range 难以覆盖的复杂写入。";

        public ToolPermission RequiredPermission => ToolPermission.Write;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var request = ExecuteScriptRequest.Parse(input);
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                return ToolCallResult.Error(Name, "脚本内容不能为空。");
            }

            var validationResult = _scriptSecurityValidator.Validate(request.Code);
            if (!validationResult.IsValid)
            {
                return ToolCallResult.Error(Name, validationResult.Message);
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
                            .ExecuteAsync(request.Code, globals, cancellationToken)
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
                            "脚本编译失败。可用全局变量：app、doc、WordApp、ActiveDoc；可调用 Write(\"文本\") 输出调试信息。"
                                + Environment.NewLine
                                + diagnostics,
                            ex);
                    }
                })
                .ConfigureAwait(false);

            var payload = new
            {
                success = executionResult.Success,
                output = executionResult.Output,
                return_value_type = executionResult.ReturnValueType
            };

            return ToolCallResult.Ok(
                JsonSerializer.Serialize(payload, JsonOptions),
                request.AffectedParagraphs.ToArray(),
                operationDescription: string.IsNullOrWhiteSpace(request.Description)
                    ? "已执行脚本写入。"
                    : request.Description);
        }
    }

    public sealed class ExecuteScriptRequest
    {
        public string Description { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;

        public List<int> AffectedParagraphs { get; } = new List<int>();

        public static ExecuteScriptRequest Parse(JsonElement input)
        {
            var request = new ExecuteScriptRequest
            {
                Description = ReadString(input, "description"),
                Code = ReadString(input, "code")
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
