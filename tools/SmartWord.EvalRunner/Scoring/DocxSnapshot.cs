using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace SmartWord.EvalRunner
{
    internal sealed class DocxSnapshot
    {
        public IReadOnlyList<DocxParagraph> Paragraphs { get; private set; } = new List<DocxParagraph>();
        public IReadOnlyList<DocxTable> Tables { get; private set; } = new List<DocxTable>();
        public IReadOnlyList<DocxHeaderFooter> Headers { get; private set; } = new List<DocxHeaderFooter>();
        public IReadOnlyList<DocxHeaderFooter> Footers { get; private set; } = new List<DocxHeaderFooter>();
        public IReadOnlyList<DocxSection> Sections { get; private set; } = new List<DocxSection>();
        public IReadOnlyList<string> FootnotesAndEndnotes { get; private set; } = new List<string>();
        public bool HasTocField { get; private set; }
        public int PageBreakCount { get; private set; }
        public string Text { get; private set; } = string.Empty;
        public string NormalizedText { get; private set; } = string.Empty;

        public static DocxSnapshot Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new DocxSnapshot();
            }

            using (var document = WordprocessingDocument.Open(path, false))
            {
                var mainPart = document.MainDocumentPart;
                var body = mainPart?.Document?.Body;
                var styleMap = BuildStyleMap(mainPart);
                var paragraphs = new List<DocxParagraph>();
                var tables = new List<DocxTable>();
                var paragraphIndex = 0;
                var tableIndex = 0;

                if (body != null)
                {
                    foreach (var child in body.ChildElements)
                    {
                        if (child is Paragraph paragraph)
                        {
                            paragraphs.Add(ReadParagraph(paragraph, ++paragraphIndex, styleMap));
                        }
                        else if (child is Table table)
                        {
                            tables.Add(ReadTable(table, ++tableIndex, styleMap));
                        }
                    }
                }

                var headers = mainPart == null
                    ? new List<DocxHeaderFooter>()
                    : mainPart.HeaderParts.Select(part => ReadHeaderFooter(part.Header, "header", styleMap)).ToList();
                var footers = mainPart == null
                    ? new List<DocxHeaderFooter>()
                    : mainPart.FooterParts.Select(part => ReadHeaderFooter(part.Footer, "footer", styleMap)).ToList();
                var text = string.Join("\n", paragraphs.Select(item => item.Text));

                return new DocxSnapshot
                {
                    Paragraphs = paragraphs,
                    Tables = tables,
                    Headers = headers,
                    Footers = footers,
                    Sections = body?.Descendants<SectionProperties>().Select(ReadSection).ToList()
                        ?? new List<DocxSection>(),
                    FootnotesAndEndnotes = ReadNotes(mainPart),
                    HasTocField = HasToc(mainPart),
                    PageBreakCount = mainPart?.Document?.Descendants<Break>().Count(item => item.Type?.Value == BreakValues.Page) ?? 0,
                    Text = text,
                    NormalizedText = Normalize(text)
                };
            }
        }

        public IReadOnlyList<DocxParagraph> FindScope(string scope)
        {
            var mapped = MapScopeName((scope ?? string.Empty).Trim());
            var start = Paragraphs.Select((paragraph, index) => new { paragraph, index })
                .FirstOrDefault(item => ContainsNormalized(item.paragraph.Text, mapped));
            if (start == null)
            {
                return Array.Empty<DocxParagraph>();
            }

            var headingLevel = GetHeadingLevel(start.paragraph);
            var end = Paragraphs.Count;
            for (var i = start.index + 1; i < Paragraphs.Count; i++)
            {
                var candidateLevel = GetHeadingLevel(Paragraphs[i]);
                if (candidateLevel > 0 && (headingLevel <= 0 || candidateLevel <= headingLevel))
                {
                    end = i;
                    break;
                }
            }

            return Paragraphs.Skip(start.index).Take(end - start.index).ToList();
        }

        public static string Normalize(string text)
        {
            return string.Concat((text ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch)));
        }

        public static bool ContainsNormalized(string text, string expected)
        {
            return Normalize(text).IndexOf(Normalize(expected), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string MapScopeName(string scope)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["party_clause"] = "合同主体",
                ["project_scope"] = "项目范围",
                ["delivery_table"] = "交付计划",
                ["payment_table"] = "付款",
                ["ip_clause"] = "知识产权",
                ["attachments"] = "附件"
            };
            return names.TryGetValue(scope, out var mapped) ? mapped : scope;
        }

        private static int GetHeadingLevel(DocxParagraph paragraph)
        {
            var style = paragraph.StyleId ?? string.Empty;
            var prefix = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ? "Heading" : "标题";
            return style.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(style.Substring(prefix.Length), out var level)
                    ? level
                    : 0;
        }

        private static Dictionary<string, StyleInfo> BuildStyleMap(MainDocumentPart mainPart)
        {
            var result = new Dictionary<string, StyleInfo>(StringComparer.OrdinalIgnoreCase);
            var styles = mainPart?.StyleDefinitionsPart?.Styles;
            if (styles == null)
            {
                return result;
            }

            foreach (var style in styles.Elements<Style>())
            {
                var id = style.StyleId?.Value;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                result[id] = new StyleInfo
                {
                    Name = style.StyleName?.Val?.Value ?? id,
                    Bold = style.StyleRunProperties?.Bold != null,
                    FontName = ReadRunFont(style.StyleRunProperties),
                    FontSizeHalfPoints = ReadFontSize(style.StyleRunProperties),
                    Alignment = NormalizeAlignment(style.StyleParagraphProperties?.Justification?.Val?.Value),
                    FirstLineChars = ReadDecimal(style.StyleParagraphProperties?.Indentation?.FirstLineChars?.Value),
                    FirstLineTwips = ReadDecimal(style.StyleParagraphProperties?.Indentation?.FirstLine?.Value),
                    LineSpacingMultiple = ReadLineSpacing(style.StyleParagraphProperties?.SpacingBetweenLines)
                };
            }

            return result;
        }

        private static DocxParagraph ReadParagraph(
            Paragraph paragraph,
            int index,
            Dictionary<string, StyleInfo> styleMap)
        {
            var properties = paragraph.ParagraphProperties;
            var styleId = properties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
            styleMap.TryGetValue(styleId, out var style);
            var runProperties = paragraph.Descendants<RunProperties>().FirstOrDefault();
            return new DocxParagraph
            {
                Index = index,
                Text = paragraph.InnerText ?? string.Empty,
                StyleId = styleId,
                StyleName = style?.Name ?? styleId,
                Bold = runProperties?.Bold != null || style?.Bold == true,
                FontName = ReadRunFont(runProperties) ?? style?.FontName ?? string.Empty,
                FontSizeHalfPoints = ReadFontSize(runProperties) ?? style?.FontSizeHalfPoints,
                Alignment = NormalizeAlignment(properties?.Justification?.Val?.Value) ?? style?.Alignment ?? string.Empty,
                FirstLineChars = ReadDecimal(properties?.Indentation?.FirstLineChars?.Value) ?? style?.FirstLineChars,
                FirstLineTwips = ReadDecimal(properties?.Indentation?.FirstLine?.Value) ?? style?.FirstLineTwips,
                LineSpacingMultiple = ReadLineSpacing(properties?.SpacingBetweenLines) ?? style?.LineSpacingMultiple,
                HighlightCount = paragraph.Descendants<Highlight>().Count()
            };
        }

        private static DocxTable ReadTable(Table table, int index, Dictionary<string, StyleInfo> styleMap)
        {
            var rows = table.Elements<TableRow>()
                .Select((row, rowIndex) => ReadRow(row, rowIndex + 1, styleMap))
                .ToList();
            return new DocxTable
            {
                Index = index,
                Rows = rows,
                HasBorders = table.GetFirstChild<TableProperties>()?.TableBorders != null,
                HasHeaderShading = rows.FirstOrDefault()?.Cells.Any(cell => !string.IsNullOrWhiteSpace(cell.Shading)) == true
            };
        }

        private static DocxTableRow ReadRow(
            TableRow row,
            int index,
            Dictionary<string, StyleInfo> styleMap)
        {
            return new DocxTableRow
            {
                Index = index,
                IsRepeatingHeader = row.TableRowProperties?.GetFirstChild<TableHeader>() != null,
                Cells = row.Elements<TableCell>()
                    .Select((cell, cellIndex) => ReadCell(cell, cellIndex + 1, styleMap))
                    .ToList()
            };
        }

        private static DocxTableCell ReadCell(
            TableCell cell,
            int index,
            Dictionary<string, StyleInfo> styleMap)
        {
            var paragraphs = cell.Elements<Paragraph>()
                .Select((paragraph, paragraphIndex) => ReadParagraph(paragraph, paragraphIndex + 1, styleMap))
                .ToList();
            return new DocxTableCell
            {
                Index = index,
                Text = string.Join("\n", paragraphs.Select(item => item.Text)),
                Paragraphs = paragraphs,
                VerticalAlignment = NormalizeAlignment(cell.TableCellProperties?.TableCellVerticalAlignment?.Val?.Value) ?? string.Empty,
                GridSpan = int.TryParse(cell.TableCellProperties?.GridSpan?.Val?.Value.ToString(), out var span) ? span : 1,
                HasVerticalMerge = cell.TableCellProperties?.VerticalMerge != null,
                Shading = cell.TableCellProperties?.Shading?.Fill?.Value ?? string.Empty
            };
        }

        private static DocxHeaderFooter ReadHeaderFooter(
            OpenXmlElement root,
            string kind,
            Dictionary<string, StyleInfo> styleMap)
        {
            var paragraphs = root?.Elements<Paragraph>()
                .Select((paragraph, index) => ReadParagraph(paragraph, index + 1, styleMap))
                .ToList() ?? new List<DocxParagraph>();
            return new DocxHeaderFooter
            {
                Kind = kind,
                Text = string.Join("\n", paragraphs.Select(item => item.Text)),
                Paragraphs = paragraphs,
                HasPageField = root?.Descendants<FieldCode>()
                    .Any(code => (code.Text ?? string.Empty).IndexOf("PAGE", StringComparison.OrdinalIgnoreCase) >= 0) == true
                    || root?.Descendants<SimpleField>()
                        .Any(field => (field.Instruction?.Value ?? string.Empty).IndexOf("PAGE", StringComparison.OrdinalIgnoreCase) >= 0) == true
            };
        }

        private static DocxSection ReadSection(SectionProperties section)
        {
            var pageSize = section.GetFirstChild<PageSize>();
            var margins = section.GetFirstChild<PageMargin>();
            var pageNumber = section.GetFirstChild<PageNumberType>();
            var headerReferences = section.Elements<HeaderReference>().ToList();
            var footerReferences = section.Elements<FooterReference>().ToList();
            return new DocxSection
            {
                HasTitlePage = section.GetFirstChild<TitlePage>() != null,
                HasHeaderReference = headerReferences.Count > 0,
                HasFooterReference = footerReferences.Count > 0,
                HasFirstHeaderReference = headerReferences.Any(item => item.Type?.Value == HeaderFooterValues.First),
                HasFirstFooterReference = footerReferences.Any(item => item.Type?.Value == HeaderFooterValues.First),
                PageNumberStart = pageNumber?.Start?.Value,
                PageNumberFormat = pageNumber?.Format?.Value.ToString() ?? string.Empty,
                WidthTwips = ToLong(pageSize?.Width),
                HeightTwips = ToLong(pageSize?.Height),
                MarginTopTwips = ToLong(margins?.Top),
                MarginBottomTwips = ToLong(margins?.Bottom),
                MarginLeftTwips = ToLong(margins?.Left),
                MarginRightTwips = ToLong(margins?.Right)
            };
        }

        private static IReadOnlyList<string> ReadNotes(MainDocumentPart mainPart)
        {
            var notes = new List<string>();
            if (mainPart?.FootnotesPart?.Footnotes != null)
            {
                notes.AddRange(mainPart.FootnotesPart.Footnotes.Elements<Footnote>().Select(item => item.InnerText ?? string.Empty));
            }

            if (mainPart?.EndnotesPart?.Endnotes != null)
            {
                notes.AddRange(mainPart.EndnotesPart.Endnotes.Elements<Endnote>().Select(item => item.InnerText ?? string.Empty));
            }

            return notes;
        }

        private static bool HasToc(MainDocumentPart mainPart)
        {
            var document = mainPart?.Document;
            return document != null
                && (document.Descendants<FieldCode>().Any(code => (code.Text ?? string.Empty).IndexOf("TOC", StringComparison.OrdinalIgnoreCase) >= 0)
                    || document.Descendants<SimpleField>().Any(field => (field.Instruction?.Value ?? string.Empty).IndexOf("TOC", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static string ReadRunFont(OpenXmlCompositeElement runProperties)
        {
            var fonts = runProperties?.GetFirstChild<RunFonts>();
            return fonts?.EastAsia?.Value ?? fonts?.Ascii?.Value ?? fonts?.HighAnsi?.Value;
        }

        private static double? ReadFontSize(OpenXmlCompositeElement runProperties)
        {
            return double.TryParse(runProperties?.GetFirstChild<FontSize>()?.Val?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                ? value
                : (double?)null;
        }

        private static decimal? ReadDecimal(object value)
        {
            return decimal.TryParse(value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
                ? result
                : (decimal?)null;
        }

        private static long? ToLong(UInt32Value value)
        {
            return value == null ? (long?)null : value.Value;
        }

        private static long? ToLong(Int32Value value)
        {
            return value == null ? (long?)null : value.Value;
        }

        private static double? ReadLineSpacing(SpacingBetweenLines spacing)
        {
            return double.TryParse(spacing?.Line?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var line)
                ? Math.Round(line / 240.0, 2)
                : (double?)null;
        }

        private static string NormalizeAlignment(object value)
        {
            var text = value?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return string.Equals(text, "both", StringComparison.OrdinalIgnoreCase)
                ? "justify"
                : text.Trim().ToLowerInvariant();
        }
    }

    internal sealed class DocxParagraph
    {
        public int Index { get; set; }
        public string Text { get; set; } = string.Empty;
        public string StyleId { get; set; } = string.Empty;
        public string StyleName { get; set; } = string.Empty;
        public bool Bold { get; set; }
        public string FontName { get; set; } = string.Empty;
        public double? FontSizeHalfPoints { get; set; }
        public string Alignment { get; set; } = string.Empty;
        public decimal? FirstLineChars { get; set; }
        public decimal? FirstLineTwips { get; set; }
        public double? LineSpacingMultiple { get; set; }
        public int HighlightCount { get; set; }
    }

    internal sealed class DocxTable
    {
        public int Index { get; set; }
        public IReadOnlyList<DocxTableRow> Rows { get; set; } = new List<DocxTableRow>();
        public bool HasBorders { get; set; }
        public bool HasHeaderShading { get; set; }
        public string Text => string.Join("\n", Rows.SelectMany(row => row.Cells).Select(cell => cell.Text));
    }

    internal sealed class DocxTableRow
    {
        public int Index { get; set; }
        public bool IsRepeatingHeader { get; set; }
        public IReadOnlyList<DocxTableCell> Cells { get; set; } = new List<DocxTableCell>();
    }

    internal sealed class DocxTableCell
    {
        public int Index { get; set; }
        public string Text { get; set; } = string.Empty;
        public IReadOnlyList<DocxParagraph> Paragraphs { get; set; } = new List<DocxParagraph>();
        public string VerticalAlignment { get; set; } = string.Empty;
        public int GridSpan { get; set; } = 1;
        public bool HasVerticalMerge { get; set; }
        public string Shading { get; set; } = string.Empty;
    }

    internal sealed class DocxHeaderFooter
    {
        public string Kind { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public IReadOnlyList<DocxParagraph> Paragraphs { get; set; } = new List<DocxParagraph>();
        public bool HasPageField { get; set; }
    }

    internal sealed class DocxSection
    {
        public bool HasTitlePage { get; set; }
        public bool HasHeaderReference { get; set; }
        public bool HasFooterReference { get; set; }
        public bool HasFirstHeaderReference { get; set; }
        public bool HasFirstFooterReference { get; set; }
        public int? PageNumberStart { get; set; }
        public string PageNumberFormat { get; set; } = string.Empty;
        public long? WidthTwips { get; set; }
        public long? HeightTwips { get; set; }
        public long? MarginTopTwips { get; set; }
        public long? MarginBottomTwips { get; set; }
        public long? MarginLeftTwips { get; set; }
        public long? MarginRightTwips { get; set; }
    }

    internal sealed class StyleInfo
    {
        public string Name { get; set; } = string.Empty;
        public bool Bold { get; set; }
        public string FontName { get; set; } = string.Empty;
        public double? FontSizeHalfPoints { get; set; }
        public string Alignment { get; set; } = string.Empty;
        public decimal? FirstLineChars { get; set; }
        public decimal? FirstLineTwips { get; set; }
        public double? LineSpacingMultiple { get; set; }
    }
}
