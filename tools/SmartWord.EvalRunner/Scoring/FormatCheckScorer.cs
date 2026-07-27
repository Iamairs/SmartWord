using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SmartWord.EvalRunner
{
    internal sealed class FormatCheckScorer : CheckScorerBase
    {
        public FormatCheckScorer()
            : base("paragraph_style", "header_text", "footer_page_number", "caption_style", "first_page_no_header_footer", "abstract_page_number", "body_page_number", "heading_style", "toc_exists", "page_setup", "footnote_or_endnote_exists", "generated_documents_or_sections", "mail_merge_fields_replaced", "title_style")
        {
        }

        public override CheckResult Score(ScoreContext context)
        {
            var type = context.Check.Value<string>("type") ?? string.Empty;
            switch (type)
            {
                case "paragraph_style": return ScoreStyle(context, FindParagraph(context.Output, context.Check.Value<string>("target")), "paragraph_style");
                case "title_style": return ScoreStyle(context, FindParagraph(context.Output, context.Check.Value<string>("target_text")), "title_style");
                case "header_text": return ScoreHeader(context);
                case "footer_page_number": return ScoreFooter(context);
                case "caption_style": return ScoreCaptions(context);
                case "first_page_no_header_footer": return ScoreFirstPage(context);
                case "abstract_page_number": return ScoreAbstractPage(context);
                case "body_page_number": return ScoreBodyPage(context);
                case "heading_style": return ScoreHeadings(context);
                case "toc_exists": return ScoreToc(context);
                case "page_setup": return ScorePageSetup(context);
                case "footnote_or_endnote_exists": return ScoreNotes(context);
                case "generated_documents_or_sections": return ScoreGenerated(context);
                case "mail_merge_fields_replaced": return ScoreMailMerge(context);
                default: return CheckResult.Unsupported(type, Points(context.Check), "格式 scorer 未识别该检查类型。");
            }
        }

        private static CheckResult ScoreHeader(ScoreContext c)
        {
            var text = c.Check.Value<string>("text") ?? string.Empty;
            var alignment = c.Check.Value<string>("alignment");
            var matches = c.Output.Headers.SelectMany(h => h.Paragraphs).Where(p => DocxSnapshot.ContainsNormalized(p.Text, text)).ToList();
            var passed = !string.IsNullOrWhiteSpace(text) && matches.Count > 0 && (string.IsNullOrWhiteSpace(alignment) || matches.Any(p => string.Equals(p.Alignment, alignment, StringComparison.OrdinalIgnoreCase)));
            return Result(c, "page", passed, passed ? "页眉文本符合要求。" : "页眉文本或对齐方式不符合要求。", text, string.Join("|", c.Output.Headers.Select(h => h.Text)));
        }

        private static CheckResult ScoreFooter(ScoreContext c)
        {
            var alignment = c.Check.Value<string>("alignment");
            var passed = c.Output.Footers.Any(f => f.HasPageField && (string.IsNullOrWhiteSpace(alignment) || f.Paragraphs.Any(p => string.Equals(p.Alignment, alignment, StringComparison.OrdinalIgnoreCase))));
            return Result(c, "page", passed, passed ? "页脚包含页码字段。" : "页脚未找到符合要求的页码字段。", "PAGE 字段", string.Join("|", c.Output.Footers.Select(f => f.Text)));
        }

        private static CheckResult ScoreCaptions(ScoreContext c)
        {
            var targets = ReadStrings(c.Check["targets"]);
            if (targets.Count == 0) return CheckResult.Unsupported(Type(c), Points(c.Check), "缺少 targets。");
            var failed = targets.Where(t =>
            {
                var p = FindParagraph(c.Output, t);
                return p == null || (p.StyleId.IndexOf("caption", StringComparison.OrdinalIgnoreCase) < 0 && p.StyleName.IndexOf("题注", StringComparison.OrdinalIgnoreCase) < 0 && p.StyleName.IndexOf("caption", StringComparison.OrdinalIgnoreCase) < 0);
            }).ToList();
            return Result(c, "format", failed.Count == 0, failed.Count == 0 ? "题注样式符合要求。" : "题注样式不匹配：" + string.Join("、", failed), string.Join("、", targets), failed.Count == 0 ? "全部匹配" : string.Join("、", failed));
        }

        private static CheckResult ScoreFirstPage(ScoreContext c)
        {
            var section = c.Output.Sections.FirstOrDefault();
            if (section == null) return CheckResult.Unsupported(Type(c), Points(c.Check), "未读取到分节属性。");
            var passed = section.HasTitlePage && !section.HasFirstHeaderReference && !section.HasFirstFooterReference;
            return Result(c, "page", passed, passed ? "首页无页眉页脚。" : "首页页眉页脚设置不符合要求。", "首页无页眉页脚", "titlePage=" + section.HasTitlePage + ", firstHeader=" + section.HasFirstHeaderReference + ", firstFooter=" + section.HasFirstFooterReference);
        }

        private static CheckResult ScoreAbstractPage(ScoreContext c)
        {
            var section = c.Output.Sections.FirstOrDefault();
            var format = c.Check.Value<string>("format");
            var passed = section != null && c.Output.Footers.Any(f => f.HasPageField) && MatchesFormat(section.PageNumberFormat, format);
            return Result(c, "page", passed, passed ? "摘要页码设置符合要求。" : "摘要页码设置不符合要求。", format ?? "页码字段", section == null ? "无分节" : section.PageNumberFormat);
        }

        private static CheckResult ScoreBodyPage(ScoreContext c)
        {
            var sectionIndex = ReadInt(c.Check["start_section"]) ?? 2;
            var section = c.Output.Sections.ElementAtOrDefault(sectionIndex - 1);
            var start = ReadInt(c.Check["start_number"]);
            var format = c.Check.Value<string>("format");
            if (section == null) return CheckResult.Unsupported(Type(c), Points(c.Check), "指定正文分节不存在。");
            var passed = (!start.HasValue || section.PageNumberStart == start.Value) && MatchesFormat(section.PageNumberFormat, format) && c.Output.Footers.Any(f => f.HasPageField);
            return Result(c, "page", passed, passed ? "正文页码设置符合要求。" : "正文页码设置不符合要求。", "start=" + start + ", format=" + format, "start=" + section.PageNumberStart + ", format=" + section.PageNumberFormat);
        }

        private static CheckResult ScoreHeadings(ScoreContext c)
        {
            var targets = ReadStrings(c.Check["targets"]);
            var style = c.Check.Value<string>("style") ?? string.Empty;
            if (targets.Count == 0 || string.IsNullOrWhiteSpace(style)) return CheckResult.Unsupported(Type(c), Points(c.Check), "缺少 targets 或 style。");
            var failed = targets.Where(t => { var p = FindParagraph(c.Output, t); return p == null || !StyleMatches(p, style); }).ToList();
            return Result(c, "format", failed.Count == 0, failed.Count == 0 ? "标题样式均符合要求。" : "标题样式不匹配：" + string.Join("、", failed), style, failed.Count == 0 ? "全部匹配" : string.Join("、", failed));
        }

        private static CheckResult ScoreToc(ScoreContext c)
        {
            var placeholder = c.Check.Value<string>("replaced_placeholder");
            var passed = c.Output.HasTocField && (string.IsNullOrWhiteSpace(placeholder) || !c.Output.Text.Contains(placeholder));
            return Result(c, "field", passed, passed ? "目录字段存在。" : "未找到目录字段或占位符仍存在。", "TOC 字段", "toc=" + c.Output.HasTocField);
        }

        private static CheckResult ScorePageSetup(ScoreContext c)
        {
            var section = c.Output.Sections.LastOrDefault();
            if (section == null) return CheckResult.Unsupported(Type(c), Points(c.Check), "未读取到页面设置。");
            var failures = new List<string>();
            if (string.Equals(c.Check.Value<string>("paper"), "A4", StringComparison.OrdinalIgnoreCase) && !(Approx(section.WidthTwips, 11906, 120) && Approx(section.HeightTwips, 16838, 120))) failures.Add("paper");
            var margins = c.Check["margins_cm"];
            if (margins is JObject marginObject) { CheckMargin(marginObject, "top", section.MarginTopTwips, failures); CheckMargin(marginObject, "bottom", section.MarginBottomTwips, failures); CheckMargin(marginObject, "left", section.MarginLeftTwips, failures); CheckMargin(marginObject, "right", section.MarginRightTwips, failures); }
            else if (ReadDouble(margins).HasValue)
            {
                var expectedTwips = ReadDouble(margins).Value * 567;
                if (!Approx(section.MarginTopTwips, expectedTwips, 80) || !Approx(section.MarginBottomTwips, expectedTwips, 80) || !Approx(section.MarginLeftTwips, expectedTwips, 80) || !Approx(section.MarginRightTwips, expectedTwips, 80)) failures.Add("margins_cm");
            }
            return Result(c, "page", failures.Count == 0, failures.Count == 0 ? "页面设置符合要求。" : "页面设置不匹配：" + string.Join(",", failures), c.Check.ToString(Newtonsoft.Json.Formatting.None), "width=" + section.WidthTwips + ", height=" + section.HeightTwips);
        }

        private static CheckResult ScoreNotes(ScoreContext c)
        {
            var source = c.Check.Value<string>("source_text") ?? string.Empty;
            var passed = c.Output.FootnotesAndEndnotes.Count > 0 && (string.IsNullOrWhiteSpace(source) || DocxSnapshot.ContainsNormalized(c.Output.Text, source));
            return Result(c, "field", passed, passed ? "脚注或尾注存在。" : "未找到脚注/尾注或源文本。", source, "notes=" + c.Output.FootnotesAndEndnotes.Count);
        }

        private static CheckResult ScoreGenerated(ScoreContext c)
        {
            var expected = ReadInt(c.Check["expected_count"]) ?? 0;
            var separator = c.Check.Value<string>("separator");
            var actual = string.IsNullOrWhiteSpace(separator) ? Math.Max(c.Output.Sections.Count, c.Output.PageBreakCount + 1) : CountOccurrences(c.Output.Text, separator);
            return Result(c, "page", actual >= expected, "生成文档/分节数量为 " + actual + "，要求至少 " + expected + "。", expected.ToString(), actual.ToString());
        }

        private static CheckResult ScoreMailMerge(ScoreContext c)
        {
            var fields = ReadStrings(c.Check["fields"]);
            if (fields.Count == 0) return CheckResult.Unsupported(Type(c), Points(c.Check), "缺少 fields。");
            var remaining = fields.Where(f => c.Output.Text.Contains("«" + f + "»") || c.Output.Text.Contains("{{" + f + "}}") || c.Output.Text.Contains("{" + f + "}")).ToList();
            return Result(c, "field", remaining.Count == 0, remaining.Count == 0 ? "邮件合并字段均已替换。" : "仍存在占位字段：" + string.Join("、", remaining), string.Join("、", fields), remaining.Count == 0 ? "全部替换" : string.Join("、", remaining));
        }

        private static CheckResult ScoreStyle(ScoreContext c, DocxParagraph paragraph, string category)
        {
            if (paragraph == null) return CheckResult.Unsupported(Type(c), Points(c.Check), "无法定位目标段落。");
            var failures = new List<string>();
            var font = c.Check.Value<string>("font_name"); var size = c.Check.Value<double?>("font_size"); var bold = c.Check.Value<bool?>("bold"); var align = c.Check.Value<string>("alignment"); var indent = c.Check.Value<decimal?>("first_line_indent_chars"); var line = c.Check.Value<double?>("line_spacing");
            if (!string.IsNullOrWhiteSpace(font) && (paragraph.FontName ?? string.Empty).IndexOf(font, StringComparison.OrdinalIgnoreCase) < 0) failures.Add("font_name");
            if (size.HasValue && (!paragraph.FontSizeHalfPoints.HasValue || Math.Abs(paragraph.FontSizeHalfPoints.Value - size.Value * 2) > 1)) failures.Add("font_size");
            if (bold.HasValue && paragraph.Bold != bold.Value) failures.Add("bold");
            if (!string.IsNullOrWhiteSpace(align) && !string.Equals(paragraph.Alignment, align, StringComparison.OrdinalIgnoreCase)) failures.Add("alignment");
            if (indent.HasValue && !IndentMatches(paragraph, indent.Value)) failures.Add("first_line_indent_chars");
            if (line.HasValue && (!paragraph.LineSpacingMultiple.HasValue || Math.Abs(paragraph.LineSpacingMultiple.Value - line.Value) > 0.08)) failures.Add("line_spacing");
            return Result(c, category, failures.Count == 0, failures.Count == 0 ? "样式符合要求。" : "样式不匹配：" + string.Join(",", failures), c.Check.ToString(Newtonsoft.Json.Formatting.None), "style=" + paragraph.StyleId + ", font=" + paragraph.FontName + ", sizeHalf=" + paragraph.FontSizeHalfPoints + ", bold=" + paragraph.Bold + ", align=" + paragraph.Alignment);
        }

        private static DocxParagraph FindParagraph(DocxSnapshot snapshot, string target) => string.IsNullOrWhiteSpace(target) ? snapshot.Paragraphs.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Text)) : snapshot.Paragraphs.FirstOrDefault(p => DocxSnapshot.ContainsNormalized(p.Text, target));
        private static bool StyleMatches(DocxParagraph p, string style) => (p.StyleId + "|" + p.StyleName).IndexOf(style ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0 || NormalizeStyle(p.StyleId) == NormalizeStyle(style) || NormalizeStyle(p.StyleName) == NormalizeStyle(style);
        private static string NormalizeStyle(string s) => (s ?? string.Empty).Replace(" ", string.Empty).Replace("标题", "heading").ToLowerInvariant();
        private static bool IndentMatches(DocxParagraph p, decimal expected) => (p.FirstLineChars.HasValue && Math.Abs(p.FirstLineChars.Value / 100m - expected) < 0.1m) || (p.FirstLineTwips.HasValue && Math.Abs(p.FirstLineTwips.Value / 210m - expected) < 0.25m);
        private static bool MatchesFormat(string actual, string expected) => string.IsNullOrWhiteSpace(expected) || (!string.IsNullOrWhiteSpace(actual) && (actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0 || (expected.IndexOf("roman", StringComparison.OrdinalIgnoreCase) >= 0 && actual.IndexOf("Roman", StringComparison.OrdinalIgnoreCase) >= 0)));
        private static int? ReadInt(JToken token) { if (token == null) return null; return int.TryParse(token.ToString(), out var value) ? value : (int?)null; }
        private static double? ReadDouble(JToken token) { if (token == null) return null; return double.TryParse(token.ToString(), out var value) ? value : (double?)null; }
        private static void CheckMargin(JObject obj, string name, long? actual, ICollection<string> failures) { var expected = obj.Value<double?>(name); if (expected.HasValue && !Approx(actual, expected.Value * 567, 80)) failures.Add(name); }
        private static bool Approx(long? actual, double expected, double tolerance) => actual.HasValue && Math.Abs(actual.Value - expected) <= tolerance;
        private static string Type(ScoreContext c) => c.Check.Value<string>("type") ?? string.Empty;
    }
}
