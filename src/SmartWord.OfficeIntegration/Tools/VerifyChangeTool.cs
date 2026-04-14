using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 对写入结果做结构化回读验证，供模型显式判断是否需要自愈。
    /// </summary>
    public sealed class VerifyChangeTool : ITool
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly WordApplicationWrapper _wordApplicationWrapper;
        private readonly JsonElement _inputSchema;

        public VerifyChangeTool(WordApplicationWrapper wordApplicationWrapper)
        {
            _wordApplicationWrapper = wordApplicationWrapper;
            _inputSchema = JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"checks\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"type\":{\"type\":\"string\"},\"paragraph_index\":{\"type\":\"integer\"},\"expected\":{\"type\":\"string\"},\"should_exist\":{\"type\":\"boolean\"}},\"required\":[\"type\",\"paragraph_index\"]}}},\"required\":[\"checks\"]}")
                .RootElement
                .Clone();
        }

        public string Name => "verify_change";

        public string Description => "回读段落文本、样式与存在性，验证写操作是否达成预期。";

        public ToolPermission RequiredPermission => ToolPermission.ReadOnly;

        public JsonElement InputSchema => _inputSchema;

        public async Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
        {
            _ = undoScope;
            cancellationToken.ThrowIfCancellationRequested();

            var request = VerifyChangeRequest.Parse(input);
            if (request.Checks.Count == 0)
            {
                return ToolCallResult.Error(Name, "至少需要提供一个 checks 项。");
            }

            var results = new List<VerifyChangeCheckResult>();
            foreach (var check in request.Checks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var paragraphExists = await _wordApplicationWrapper
                    .ParagraphExistsAsync(check.ParagraphIndex)
                    .ConfigureAwait(false);
                var actualText = paragraphExists
                    ? await _wordApplicationWrapper.GetParagraphTextAsync(check.ParagraphIndex).ConfigureAwait(false)
                    : string.Empty;
                var actualStyle = paragraphExists
                    ? await _wordApplicationWrapper.GetParagraphStyleAsync(check.ParagraphIndex).ConfigureAwait(false)
                    : string.Empty;

                results.Add(VerifyChangeEvaluator.Evaluate(check, paragraphExists, actualText, actualStyle));
            }

            var payload = new
            {
                all_passed = results.All(item => item.Passed),
                results = results.Select(item => new
                {
                    check_index = item.CheckIndex,
                    type = item.Type,
                    paragraph_index = item.ParagraphIndex,
                    passed = item.Passed,
                    actual = item.Actual,
                    expected = item.Expected,
                    hint = item.Hint
                })
            };

            return ToolCallResult.Ok(
                JsonSerializer.Serialize(payload, JsonOptions),
                results.Select(item => item.ParagraphIndex).Distinct().ToArray(),
                operationDescription: "已完成改动验证。");
        }
    }

    public sealed class VerifyChangeRequest
    {
        public List<VerifyChangeCheck> Checks { get; } = new List<VerifyChangeCheck>();

        public static VerifyChangeRequest Parse(JsonElement input)
        {
            var request = new VerifyChangeRequest();
            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty("checks", out var checksElement)
                || checksElement.ValueKind != JsonValueKind.Array)
            {
                return request;
            }

            var index = 0;
            foreach (var item in checksElement.EnumerateArray())
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

                request.Checks.Add(new VerifyChangeCheck
                {
                    CheckIndex = index,
                    Type = type.Trim(),
                    ParagraphIndex = paragraphIndex.Value,
                    Expected = ReadString(item, "expected"),
                    ShouldExist = ReadNullableBool(item, "should_exist")
                });
                index++;
            }

            return request;
        }

        private static string ReadString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return property.GetString() ?? string.Empty;
        }

        private static int? ReadInt(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.Number
                || !property.TryGetInt32(out var value))
            {
                return null;
            }

            return value;
        }

        private static bool? ReadNullableBool(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            return null;
        }
    }

    public sealed class VerifyChangeCheck
    {
        public int CheckIndex { get; set; }

        public string Type { get; set; } = string.Empty;

        public int ParagraphIndex { get; set; }

        public string Expected { get; set; } = string.Empty;

        public bool? ShouldExist { get; set; }
    }

    public sealed class VerifyChangeCheckResult
    {
        public int CheckIndex { get; set; }

        public string Type { get; set; } = string.Empty;

        public int ParagraphIndex { get; set; }

        public bool Passed { get; set; }

        public string Actual { get; set; } = string.Empty;

        public string Expected { get; set; } = string.Empty;

        public string Hint { get; set; } = string.Empty;
    }

    public static class VerifyChangeEvaluator
    {
        public static VerifyChangeCheckResult Evaluate(
            VerifyChangeCheck check,
            bool paragraphExists,
            string actualText,
            string actualStyle)
        {
            var normalizedType = (check?.Type ?? string.Empty).Trim().ToLowerInvariant();
            var expected = check?.Expected ?? string.Empty;
            var result = new VerifyChangeCheckResult
            {
                CheckIndex = check == null ? -1 : check.CheckIndex,
                Type = normalizedType,
                ParagraphIndex = check == null ? -1 : check.ParagraphIndex,
                Expected = normalizedType == "paragraph_exists"
                    ? ((check?.ShouldExist ?? true) ? "true" : "false")
                    : expected
            };

            switch (normalizedType)
            {
                case "text_contains":
                    result.Actual = actualText ?? string.Empty;
                    result.Passed = paragraphExists
                        && !string.IsNullOrEmpty(expected)
                        && result.Actual.IndexOf(expected, StringComparison.Ordinal) >= 0;
                    result.Hint = result.Passed
                        ? string.Empty
                        : "文本未包含预期内容，建议先回读目标段落，再检查是否写入到了错误位置。";
                    return result;
                case "text_equals":
                    result.Actual = actualText ?? string.Empty;
                    result.Passed = paragraphExists
                        && string.Equals(result.Actual, expected, StringComparison.Ordinal);
                    result.Hint = result.Passed
                        ? string.Empty
                        : "文本与预期不完全一致，建议检查是否残留了原有内容或换行。";
                    return result;
                case "text_not_contains":
                    result.Actual = actualText ?? string.Empty;
                    result.Passed = paragraphExists
                        && (string.IsNullOrEmpty(expected)
                            || result.Actual.IndexOf(expected, StringComparison.Ordinal) < 0);
                    result.Hint = result.Passed
                        ? string.Empty
                        : "目标文本仍然存在，建议改用更精确的范围写入或补充删除操作。";
                    return result;
                case "style_equals":
                    result.Actual = actualStyle ?? string.Empty;
                    result.Passed = paragraphExists
                        && string.Equals(result.Actual, expected, StringComparison.OrdinalIgnoreCase);
                    result.Hint = result.Passed
                        ? string.Empty
                        : "段落样式未达到预期，建议确认样式名称是否与 Word 中的本地样式名一致。";
                    return result;
                case "paragraph_exists":
                    result.Actual = paragraphExists ? "true" : "false";
                    var shouldExist = check?.ShouldExist ?? true;
                    result.Passed = paragraphExists == shouldExist;
                    result.Hint = result.Passed
                        ? string.Empty
                        : (shouldExist
                            ? "目标段落不存在，建议先确认段落索引是否仍然有效。"
                            : "目标段落仍然存在，删除操作可能没有真正命中段落标记。");
                    return result;
                default:
                    result.Actual = string.Empty;
                    result.Passed = false;
                    result.Hint = "未知的验证类型，请使用 text_contains、text_equals、text_not_contains、style_equals 或 paragraph_exists。";
                    return result;
            }
        }
    }
}
