using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.OfficeIntegration.Tools
{
    /// <summary>
    /// 提供最小可控的范围级写入能力。
    /// </summary>
    public sealed class PatchRangeTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;

        public PatchRangeTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"description\":{\"type\":\"string\",\"description\":\"本次写操作的简要说明。\"},\"operations\":{\"type\":\"array\",\"description\":\"按顺序执行的写入操作列表。paragraph_index 使用 0-based 段落索引，即第一段是 0。\",\"items\":{\"type\":\"object\",\"properties\":{\"type\":{\"type\":\"string\",\"description\":\"支持 replace_text、insert_paragraph_after、set_paragraph_style、delete_paragraph；同时兼容 replace、set_text、insert_after、set_style、delete 等常见别名。优先输出标准名。\"},\"paragraph_index\":{\"type\":\"integer\",\"description\":\"0-based 段落索引。第一段是 0，不是 1。\"},\"text\":{\"type\":\"string\",\"description\":\"目标文本内容。replace_text 会整段替换，insert_paragraph_after 会写入新段落文本。\"},\"style\":{\"type\":\"string\",\"description\":\"Word 中可识别的段落样式名称，例如 Heading 1。\"}},\"required\":[\"type\",\"paragraph_index\"]}}},\"required\":[\"operations\"]}")
                .RootElement
                .Clone();
        }

        public string Name => "patch_range";

        public string Description => "以最小风险执行段落替换、插入、样式设置与删除。所有 paragraph_index 都使用 0-based 段落索引。";

        public ToolPermission RequiredPermission => ToolPermission.Write;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

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
                dynamic document = documentObject;
                dynamic paragraphs = null;
                dynamic paragraph = null;
                dynamic range = null;
                dynamic insertedParagraphs = null;
                dynamic insertedParagraph = null;
                dynamic insertedRange = null;

                try
                {
                    if (document == null)
                    {
                        return PatchRangeExecutionResult.Fail(operation.ParagraphIndex, "当前没有活动文档。");
                    }

                    paragraphs = document.Paragraphs;
                    var paragraphCount = paragraphs == null ? 0 : Convert.ToInt32(paragraphs.Count);
                    if (operation.ParagraphIndex < 0 || operation.ParagraphIndex >= paragraphCount)
                    {
                        return PatchRangeExecutionResult.Fail(operation.ParagraphIndex, "目标段落索引超出范围。");
                    }

                    paragraph = paragraphs[operation.ParagraphIndex + 1];
                    range = paragraph == null ? null : paragraph.Range;
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
                            insertedParagraphs = document.Paragraphs;
                            insertedParagraph = insertedParagraphs[operation.ParagraphIndex + 2];
                            insertedRange = insertedParagraph == null ? null : insertedParagraph.Range;
                            if (insertedRange == null)
                            {
                                return PatchRangeExecutionResult.Fail(operation.ParagraphIndex, "插入新段落后无法读取结果。");
                            }

                            insertedRange.Text = NormalizeTextForParagraph(operation.Text) + "\r";
                            if (!string.IsNullOrWhiteSpace(operation.Style))
                            {
                                insertedRange.set_Style(operation.Style);
                            }

                            return PatchRangeExecutionResult.Ok(operation.ParagraphIndex + 1, "新段落已插入。");
                        case "set_paragraph_style":
                            range.set_Style(operation.Style ?? string.Empty);
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
                finally
                {
                    TryReleaseComObject(insertedRange);
                    TryReleaseComObject(insertedParagraph);
                    TryReleaseComObject(insertedParagraphs);
                    TryReleaseComObject(range);
                    TryReleaseComObject(paragraph);
                    TryReleaseComObject(paragraphs);
                }
            });
        }

        private static string NormalizeTextForParagraph(string text)
        {
            return (text ?? string.Empty).TrimEnd('\r', '\n');
        }

        private static void TryReleaseComObject(object comObject)
        {
            if (comObject == null)
            {
                return;
            }

            try
            {
                if (Marshal.IsComObject(comObject))
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch
            {
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
