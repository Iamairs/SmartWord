using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SmartWord.OfficeIntegration.Models;

namespace SmartWord.OfficeIntegration.Reading
{
    /// <summary>
    /// 在段落快照上执行纯文本或正则搜索。
    /// </summary>
    public sealed class ParagraphSearchEngine
    {
        public ParagraphSearchExecutionResult Search(
            IReadOnlyList<ParagraphSnapshot> paragraphs,
            string keyword,
            bool useRegex,
            int maxHitParagraphs)
        {
            var result = new ParagraphSearchExecutionResult();
            if (paragraphs == null || paragraphs.Count == 0 || string.IsNullOrWhiteSpace(keyword))
            {
                return result;
            }

            Regex regex = null;
            if (useRegex)
            {
                try
                {
                    regex = new Regex(keyword, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch (ArgumentException ex)
                {
                    result.ErrorMessage = "正则表达式无效：" + ex.Message;
                    return result;
                }
            }

            var safeLimit = Math.Max(1, maxHitParagraphs);
            foreach (var paragraph in paragraphs)
            {
                var matches = useRegex
                    ? FindRegexMatches(regex, paragraph.Text)
                    : FindKeywordMatches(paragraph.Text, keyword);
                if (matches.Count == 0)
                {
                    continue;
                }

                result.TotalHitParagraphs++;
                result.TotalMatches += matches.Count;
                if (result.Results.Count < safeLimit)
                {
                    result.Results.Add(new ParagraphSnapshot
                    {
                        Index = paragraph.Index,
                        Style = paragraph.Style,
                        Text = paragraph.Text,
                        Start = paragraph.Start,
                        End = paragraph.End,
                        Matches = matches
                    });
                }
                else
                {
                    result.IsTruncated = true;
                }
            }

            return result;
        }

        private static IList<TextMatchSnapshot> FindRegexMatches(Regex regex, string text)
        {
            return regex
                .Matches(text ?? string.Empty)
                .Cast<Match>()
                .Where(item => item.Success)
                .Select(item => new TextMatchSnapshot
                {
                    Start = item.Index,
                    Length = item.Length
                })
                .ToList();
        }

        private static IList<TextMatchSnapshot> FindKeywordMatches(string text, string keyword)
        {
            var matches = new List<TextMatchSnapshot>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword))
            {
                return matches;
            }

            var index = 0;
            while (index < text.Length)
            {
                var foundIndex = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
                if (foundIndex < 0)
                {
                    break;
                }

                matches.Add(new TextMatchSnapshot
                {
                    Start = foundIndex,
                    Length = keyword.Length
                });
                index = foundIndex + Math.Max(1, keyword.Length);
            }

            return matches;
        }
    }

    /// <summary>
    /// 表示段落搜索执行结果。
    /// </summary>
    public sealed class ParagraphSearchExecutionResult
    {
        public string ErrorMessage { get; set; } = string.Empty;

        public int TotalHitParagraphs { get; set; }

        public int TotalMatches { get; set; }

        public bool IsTruncated { get; set; }

        public IList<ParagraphSnapshot> Results { get; } = new List<ParagraphSnapshot>();
    }
}
