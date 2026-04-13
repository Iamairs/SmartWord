using System;
using System.Collections.Generic;
using System.Linq;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.Models;

namespace SmartWord.OfficeIntegration.Reading
{
    /// <summary>
    /// 统一解析按标题、段落、光标或选区读取的范围。
    /// </summary>
    public sealed class ReadScopeResolver
    {
        public ResolvedReadScope Resolve(
            ReadScope scope,
            int paragraphCount,
            IReadOnlyList<DocumentHeading> headings,
            int cursorParagraphIndex,
            SelectionSnapshot selection,
            ReadDiagnostics diagnostics)
        {
            if (paragraphCount <= 0)
            {
                return new ResolvedReadScope();
            }

            var normalizedScope = scope ?? new ReadScope();
            var safeDiagnostics = diagnostics ?? new ReadDiagnostics();

            if (!string.IsNullOrWhiteSpace(normalizedScope.Heading))
            {
                var matchedHeading = headings?
                    .FirstOrDefault(item => string.Equals(item.Text, normalizedScope.Heading, StringComparison.OrdinalIgnoreCase));
                var isExactMatch = matchedHeading != null;
                if (matchedHeading == null)
                {
                    matchedHeading = headings?
                        .FirstOrDefault(item => item.Text.IndexOf(normalizedScope.Heading, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (matchedHeading != null)
                    {
                        safeDiagnostics.AddWarning("标题未精确命中，已按包含关系回退。");
                    }
                    else
                    {
                        safeDiagnostics.AddWarning("未找到指定标题，已回退到默认读取范围。");
                    }
                }

                if (matchedHeading != null)
                {
                    var endParagraph = paragraphCount - 1;
                    foreach (var nextHeading in headings.Where(item => item.ParagraphIndex > matchedHeading.ParagraphIndex))
                    {
                        var shouldStop = normalizedScope.IncludeSubsections
                            ? nextHeading.Level <= matchedHeading.Level
                            : true;
                        if (shouldStop)
                        {
                            endParagraph = Math.Max(matchedHeading.ParagraphIndex, nextHeading.ParagraphIndex - 1);
                            break;
                        }
                    }

                    if (!isExactMatch && string.IsNullOrWhiteSpace(matchedHeading.Text))
                    {
                        safeDiagnostics.AddWarning("标题匹配失败，已使用默认范围。");
                    }

                    return new ResolvedReadScope
                    {
                        FromParagraph = matchedHeading.ParagraphIndex,
                        ToParagraph = endParagraph,
                        HeadingText = matchedHeading.Text
                    };
                }
            }

            if (normalizedScope.SelectionOnly)
            {
                if (selection != null && selection.ParagraphIndex >= 0)
                {
                    var startParagraph = selection.StartParagraphIndex >= 0
                        ? selection.StartParagraphIndex
                        : selection.ParagraphIndex;
                    var endParagraph = selection.EndParagraphIndex >= startParagraph
                        ? selection.EndParagraphIndex
                        : startParagraph;
                    if (!selection.HasSelection)
                    {
                        safeDiagnostics.AddWarning("当前无显式选区，已回退到光标所在段落。");
                    }

                    return new ResolvedReadScope
                    {
                        FromParagraph = Clamp(startParagraph, 0, paragraphCount - 1),
                        ToParagraph = Clamp(endParagraph, 0, paragraphCount - 1)
                    };
                }

                safeDiagnostics.AddWarning("当前无法定位选区，已回退到默认读取范围。");
            }

            if (normalizedScope.FromParagraph.HasValue || normalizedScope.ToParagraph.HasValue)
            {
                var requestedStart = normalizedScope.FromParagraph ?? 0;
                var requestedEnd = normalizedScope.ToParagraph ?? requestedStart;
                var start = Clamp(requestedStart, 0, paragraphCount - 1);
                var end = Clamp(requestedEnd, 0, paragraphCount - 1);

                if (requestedStart != start || requestedEnd != end)
                {
                    safeDiagnostics.AddWarning("段落范围已自动裁剪到文档边界内。");
                }

                if (end < start)
                {
                    end = start;
                    safeDiagnostics.AddWarning("to_para 小于 from_para，已自动调整为单段读取。");
                }

                return new ResolvedReadScope
                {
                    FromParagraph = start,
                    ToParagraph = end
                };
            }

            if (normalizedScope.AroundCursor)
            {
                var safeCursor = cursorParagraphIndex >= 0 ? cursorParagraphIndex : 0;
                if (cursorParagraphIndex < 0)
                {
                    safeDiagnostics.AddWarning("当前无法定位光标段落，已回退到文档开头附近。");
                }

                var window = Math.Max(1, normalizedScope.ContextWindow);
                return new ResolvedReadScope
                {
                    FromParagraph = Math.Max(0, safeCursor - window),
                    ToParagraph = Math.Min(paragraphCount - 1, safeCursor + window)
                };
            }

            var defaultWindow = Math.Max(1, normalizedScope.ContextWindow);
            safeDiagnostics.AddWarning("未指定读取范围，已回退到文档开头附近。");
            return new ResolvedReadScope
            {
                FromParagraph = 0,
                ToParagraph = Math.Min(paragraphCount - 1, (defaultWindow * 2))
            };
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Min(max, Math.Max(min, value));
        }
    }
}
