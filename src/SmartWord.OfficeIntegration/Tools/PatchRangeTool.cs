using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.ComInterop;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.OfficeIntegration.Tools
{
    /// <summary>
    /// 提供最小可控的范围级写入能力。
    /// </summary>
    public sealed class PatchRangeTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = ToolJsonOptions.Default;

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;

        public PatchRangeTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"description\":{\"type\":\"string\",\"description\":\"本次写操作的简要说明，面向用户描述目标范围和结果。\"},\"operations\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":20,\"description\":\"按顺序执行的写入操作列表。必须传真实 JSON 数组，不要传字符串化后的 JSON。所有 paragraph_index 使用 0-based。前一步插入或删除会改变后续索引；同一目标的一组安全改动才放在同一次调用中。\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"type\":{\"type\":\"string\",\"enum\":[\"replace_text\",\"insert_paragraph_after\",\"set_paragraph_style\",\"delete_paragraph\"],\"description\":\"标准操作名。replace_text/insert_paragraph_after 需要 text；set_paragraph_style 需要 style；delete_paragraph 不需要 text/style。\"},\"paragraph_index\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"0-based 非负段落索引。第一段是 0，不是 1。\"},\"text\":{\"type\":\"string\",\"description\":\"replace_text 的替换文本，或 insert_paragraph_after 的新段落文本。允许为空字符串，但字段必须存在。\"},\"style\":{\"type\":\"string\",\"minLength\":1,\"description\":\"Word 可识别的段落样式名称，例如 Heading 1。set_paragraph_style 必填；insert_paragraph_after 可选。\"}},\"required\":[\"type\",\"paragraph_index\"]}}},\"required\":[\"operations\"]}")
                .RootElement
                .Clone();
        }

        public string Name => "patch_range";

        public string Description => "以最小风险执行段落替换、插入、样式设置与删除。operations 必须传真正的 JSON 数组，不要传字符串化后的 JSON；所有 paragraph_index 都使用 0-based 段落索引。当前批处理按实时文档顺序执行，前一步对结构的影响会改变后续索引。";

        public ToolPermission RequiredPermission => ToolPermission.DocumentPatchWrite;

        public bool IsVisibleToModel => true;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var operationsError = ValidateOperationsShape(input);
            if (!string.IsNullOrWhiteSpace(operationsError))
            {
                return ToolCallResult.Error(Name, operationsError);
            }

            var operationContractError = ValidateOperationContracts(input);
            if (!string.IsNullOrWhiteSpace(operationContractError))
            {
                return ToolCallResult.Error(Name, operationContractError);
            }

            var request = PatchRangeRequest.Parse(input);
            if (request.Operations.Count == 0)
            {
                return ToolCallResult.Error(Name, "至少需要提供一个 operations 项。");
            }

            var results = new List<PatchRangeOperationResult>();
            var affectedParagraphs = new HashSet<int>();

            foreach (var operation in request.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                PatchRangeExecutionResult executionResult;
                try
                {
                    executionResult = await ExecuteOperationAsync(operation).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    executionResult = new PatchRangeExecutionResult
                    {
                        Success = false,
                        AffectedParagraphIndex = operation.ParagraphIndex,
                        Message = ex.Message
                    };
                }

                if (executionResult.AffectedParagraphIndex >= 0)
                {
                    affectedParagraphs.Add(executionResult.AffectedParagraphIndex);
                }

                results.Add(new PatchRangeOperationResult
                {
                    Index = operation.Index,
                    Type = operation.Type,
                    ParagraphIndex = operation.ParagraphIndex,
                    Success = executionResult.Success,
                    Message = executionResult.Message,
                    AffectedParagraphIndex = executionResult.AffectedParagraphIndex
                });
            }

            var applied = results.Count(item => item.Success);
            var failed = results.Count - applied;
            var payload = new
            {
                success = failed == 0,
                applied,
                failed,
                affected_paragraphs = affectedParagraphs.OrderBy(item => item).ToArray(),
                results = results.Select(item => new
                {
                    index = item.Index,
                    type = item.Type,
                    paragraph_index = item.ParagraphIndex,
                    success = item.Success,
                    message = item.Message,
                    affected_paragraph_index = item.AffectedParagraphIndex
                })
            };

            var operationDescription = string.IsNullOrWhiteSpace(request.Description)
                ? $"已执行 {request.Operations.Count} 个范围写入操作。"
                : request.Description;

            return ToolCallResult.Ok(
                JsonSerializer.Serialize(payload, JsonOptions),
                affectedParagraphs.OrderBy(item => item).ToArray(),
                operationDescription: operationDescription);
        }

        private Task<PatchRangeExecutionResult> ExecuteOperationAsync(PatchRangeOperation operation)
        {
            return _wordApplicationWrapper.InvokeWithActiveDocumentAsync((wordApplicationObject, documentObject) =>
            {
                _ = wordApplicationObject;
                dynamic document = documentObject;
                using (var comScope = new ComScope())
                {
                    if (document == null)
                    {
                        return PatchRangeExecutionResult.Fail(operation.ParagraphIndex, "当前没有活动文档。");
                    }

                    dynamic paragraphs = comScope.Track((object)document.Paragraphs, "PatchRangeTool.Paragraphs");
                    var paragraphCount = paragraphs == null ? 0 : Convert.ToInt32(paragraphs.Count);
                    if (operation.ParagraphIndex < 0 || operation.ParagraphIndex >= paragraphCount)
                    {
                        return PatchRangeExecutionResult.Fail(operation.ParagraphIndex, "目标段落索引超出范围。");
                    }

                    dynamic paragraph = comScope.Track(
                        (object)paragraphs[operation.ParagraphIndex + 1],
                        "PatchRangeTool.Paragraph");
                    dynamic range = comScope.Track(
                        paragraph == null ? null : (object)paragraph.Range,
                        "PatchRangeTool.Range");
                    if (range == null)
                    {
                        return PatchRangeExecutionResult.Fail(operation.ParagraphIndex, "无法获取目标段落范围。");
                    }

                    switch ((operation.Type ?? string.Empty).Trim().ToLowerInvariant())
                    {
                        case "replace_text":
                            range.Text = NormalizeTextForParagraph(operation.Text) + "\r";
                            return PatchRangeExecutionResult.Ok(operation.ParagraphIndex, "段落文本已替换。");
                        case "insert_paragraph_after":
                            range.InsertParagraphAfter();
                            dynamic insertedParagraph = comScope.Track(
                                (object)paragraphs[operation.ParagraphIndex + 2],
                                "PatchRangeTool.InsertedParagraph");
                            dynamic insertedRange = comScope.Track(
                                insertedParagraph == null ? null : (object)insertedParagraph.Range,
                                "PatchRangeTool.InsertedRange");
                            if (insertedRange == null)
                            {
                                return PatchRangeExecutionResult.Fail(operation.ParagraphIndex, "插入新段落后无法读取结果。");
                            }

                            insertedRange.Text = NormalizeTextForParagraph(operation.Text) + "\r";
                            if (!string.IsNullOrWhiteSpace(operation.Style))
                            {
                                SetComProperty((object)insertedRange, "Style", operation.Style);
                            }

                            return PatchRangeExecutionResult.Ok(operation.ParagraphIndex + 1, "新段落已插入。");
                        case "set_paragraph_style":
                            SetComProperty((object)range, "Style", operation.Style ?? string.Empty);
                            return PatchRangeExecutionResult.Ok(operation.ParagraphIndex, "段落样式已更新。");
                        case "delete_paragraph":
                            range.Delete();
                            return PatchRangeExecutionResult.Ok(operation.ParagraphIndex, "段落已删除。");
                        default:
                            return PatchRangeExecutionResult.Fail(
                                operation.ParagraphIndex,
                                "未知的操作类型。当前支持 replace_text、insert_paragraph_after、set_paragraph_style、delete_paragraph。");
                    }
                }
            });
        }

        private static void SetComProperty(object target, string propertyName, object value)
        {
            if (target == null)
            {
                throw new InvalidOperationException("Word COM 目标对象为空，无法设置属性 " + propertyName + "。");
            }

            target.GetType().InvokeMember(
                propertyName,
                BindingFlags.SetProperty,
                null,
                target,
                new[] { value });
        }

        private static string NormalizeTextForParagraph(string text)
        {
            return (text ?? string.Empty).TrimEnd('\r', '\n');
        }

        private static string ValidateOperationsShape(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty("operations", out var operationsElement))
            {
                return "operations 是必填字段，必须传 JSON 数组。";
            }

            if (operationsElement.ValueKind == JsonValueKind.String)
            {
                return "operations 必须是 JSON 数组，不要传字符串化后的 JSON。正确示例：\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":3,\"text\":\"...\"}]。";
            }

            if (operationsElement.ValueKind != JsonValueKind.Array)
            {
                return "operations 必须是 JSON 数组。";
            }

            return string.Empty;
        }

        private static string ValidateOperationContracts(JsonElement input)
        {
            var operations = input.GetProperty("operations");
            if (operations.GetArrayLength() == 0)
            {
                return "operations 至少需要包含一个写入操作。";
            }

            if (operations.GetArrayLength() > 20)
            {
                return "operations 最多包含 20 个操作；请按阶段拆分任务。";
            }

            var index = 0;
            foreach (var item in operations.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return $"operations[{index}] 必须是 JSON 对象。";
                }

                var type = item.TryGetProperty("type", out var typeElement)
                    && typeElement.ValueKind == JsonValueKind.String
                    ? NormalizeOperationType(typeElement.GetString())
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(type))
                {
                    return $"operations[{index}].type 必须是 replace_text、insert_paragraph_after、set_paragraph_style 或 delete_paragraph。";
                }

                if (type != "replace_text"
                    && type != "insert_paragraph_after"
                    && type != "set_paragraph_style"
                    && type != "delete_paragraph")
                {
                    return $"operations[{index}].type 不支持“{type}”；请使用标准操作名，不要依赖未声明的别名。";
                }

                if (!item.TryGetProperty("paragraph_index", out var paragraphElement)
                    || paragraphElement.ValueKind != JsonValueKind.Number
                    || !paragraphElement.TryGetInt32(out var paragraphIndex)
                    || paragraphIndex < 0)
                {
                    return $"operations[{index}].paragraph_index 必须是 0-based 非负整数。";
                }

                if ((type == "replace_text" || type == "insert_paragraph_after")
                    && (!item.TryGetProperty("text", out var textElement)
                        || textElement.ValueKind != JsonValueKind.String))
                {
                    return $"operations[{index}] 的 {type} 必须提供 text 字段，且 text 必须是字符串。";
                }

                if (type == "set_paragraph_style"
                    && (!item.TryGetProperty("style", out var styleElement)
                        || styleElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(styleElement.GetString())))
                {
                    return $"operations[{index}] 的 set_paragraph_style 必须提供非空 style 字段。";
                }

                index++;
            }

            return string.Empty;
        }

        private static string NormalizeOperationType(string type)
        {
            var normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "replace":
                case "set_text":
                    return "replace_text";
                case "insert_after":
                case "append_paragraph_after":
                    return "insert_paragraph_after";
                case "set_style":
                case "apply_style":
                    return "set_paragraph_style";
                case "delete":
                case "remove_paragraph":
                    return "delete_paragraph";
                default:
                    return normalized;
            }
        }

    }

    public sealed class PatchRangeRequest
    {
        public string Description { get; set; } = string.Empty;

        public List<PatchRangeOperation> Operations { get; } = new List<PatchRangeOperation>();

        public static PatchRangeRequest Parse(JsonElement input)
        {
            var request = new PatchRangeRequest
            {
                Description = ReadString(input, "description")
            };

            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty("operations", out var operationsElement)
                || operationsElement.ValueKind != JsonValueKind.Array)
            {
                return request;
            }

            var index = 0;
            foreach (var item in operationsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    index++;
                    continue;
                }

                var type = ReadString(item, "type");
                var paragraphIndex = ReadInt(item, "paragraph_index");
                if (string.IsNullOrWhiteSpace(type) || !paragraphIndex.HasValue)
                {
                    index++;
                    continue;
                }

                request.Operations.Add(new PatchRangeOperation
                {
                    Index = index,
                    Type = NormalizeOperationType(type),
                    ParagraphIndex = paragraphIndex.Value,
                    Text = ReadString(item, "text"),
                    Style = ReadString(item, "style")
                });
                index++;
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

        private static int? ReadInt(JsonElement input, string propertyName)
        {
            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.Number
                || !property.TryGetInt32(out var value))
            {
                return null;
            }

            return value;
        }

        private static string NormalizeOperationType(string type)
        {
            var normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "replace":
                case "set_text":
                    return "replace_text";
                case "insert_after":
                case "append_paragraph_after":
                    return "insert_paragraph_after";
                case "set_style":
                case "apply_style":
                    return "set_paragraph_style";
                case "delete":
                case "remove_paragraph":
                    return "delete_paragraph";
                default:
                    return normalized;
            }
        }
    }

    public sealed class PatchRangeOperation
    {
        public int Index { get; set; }

        public string Type { get; set; } = string.Empty;

        public int ParagraphIndex { get; set; }

        public string Text { get; set; } = string.Empty;

        public string Style { get; set; } = string.Empty;
    }

    public sealed class PatchRangeOperationResult
    {
        public int Index { get; set; }

        public string Type { get; set; } = string.Empty;

        public int ParagraphIndex { get; set; }

        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        public int AffectedParagraphIndex { get; set; } = -1;
    }

    internal sealed class PatchRangeExecutionResult
    {
        public bool Success { get; set; }

        public int AffectedParagraphIndex { get; set; } = -1;

        public string Message { get; set; } = string.Empty;

        public static PatchRangeExecutionResult Ok(int affectedParagraphIndex, string message)
        {
            return new PatchRangeExecutionResult
            {
                Success = true,
                AffectedParagraphIndex = affectedParagraphIndex,
                Message = message
            };
        }

        public static PatchRangeExecutionResult Fail(int affectedParagraphIndex, string message)
        {
            return new PatchRangeExecutionResult
            {
                Success = false,
                AffectedParagraphIndex = affectedParagraphIndex,
                Message = message
            };
        }
    }
}
