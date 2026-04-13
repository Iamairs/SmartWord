using System.Collections.Generic;
using System.Linq;
using SmartWord.Core.Models;

namespace SmartWord.OfficeIntegration.Reading
{
    /// <summary>
    /// 负责根据标题列表推导段落所在章节路径。
    /// </summary>
    public static class DocumentSectionPathResolver
    {
        public static string ResolveSectionPath(IReadOnlyList<DocumentHeading> headings, int paragraphIndex)
        {
            if (headings == null || headings.Count == 0 || paragraphIndex < 0)
            {
                return string.Empty;
            }

            var stack = new List<DocumentHeading>();
            foreach (var heading in headings
                .Where(item => item.ParagraphIndex <= paragraphIndex)
                .OrderBy(item => item.ParagraphIndex))
            {
                while (stack.Count > 0 && stack[stack.Count - 1].Level >= heading.Level)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                stack.Add(heading);
            }

            return stack.Count == 0
                ? string.Empty
                : string.Join(" > ", stack.Select(item => item.Text));
        }

        public static DocumentHeading ResolveNearestHeading(IReadOnlyList<DocumentHeading> headings, int paragraphIndex)
        {
            if (headings == null || headings.Count == 0 || paragraphIndex < 0)
            {
                return null;
            }

            return headings
                .Where(item => item.ParagraphIndex <= paragraphIndex)
                .OrderByDescending(item => item.ParagraphIndex)
                .FirstOrDefault();
        }
    }
}
