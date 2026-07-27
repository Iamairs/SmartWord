using System;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace SmartWord.EvalRunner.Tests
{
    public sealed class DocxSnapshotTests
    {
        [Fact]
        public void Load_包含常用Word结构_生成完整快照()
        {
            var directory = Path.Combine(Path.GetTempPath(), "SmartWord.DocxSnapshot.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "snapshot.docx");
            try
            {
                CreateDocument(path);
                var snapshot = DocxSnapshot.Load(path);

                Assert.Contains("正文内容", snapshot.Text);
                Assert.Single(snapshot.Tables);
                Assert.True(snapshot.Tables[0].HasBorders);
                Assert.True(snapshot.Tables[0].HasHeaderShading);
                Assert.Single(snapshot.Headers);
                Assert.Contains("公司页眉", snapshot.Headers[0].Text);
                Assert.Single(snapshot.Footers);
                Assert.True(snapshot.Footers[0].HasPageField);
                Assert.True(snapshot.HasTocField);
                Assert.Equal(1, snapshot.PageBreakCount);
                Assert.Contains(snapshot.Paragraphs, paragraph => paragraph.HighlightCount == 1);
                Assert.Contains(snapshot.FootnotesAndEndnotes, note => note.Contains("脚注内容"));
                Assert.True(snapshot.Sections[0].HasTitlePage);
                Assert.Equal(1, snapshot.Sections[0].PageNumberStart);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void CreateDocument(string path)
        {
            using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                var headerPart = main.AddNewPart<HeaderPart>();
                headerPart.Header = new Header(new Paragraph(new Run(new Text("公司页眉"))));
                var footerPart = main.AddNewPart<FooterPart>();
                footerPart.Footer = new Footer(new Paragraph(new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }), new Run(new FieldCode(" PAGE ")), new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
                var footnotesPart = main.AddNewPart<FootnotesPart>();
                footnotesPart.Footnotes = new Footnotes(new Footnote(new Paragraph(new Run(new Text("脚注内容")))) { Id = 1 });

                var body = new Body(
                    new Paragraph(new Run(new RunProperties(new Highlight { Val = HighlightColorValues.Yellow }), new Text("正文内容"))),
                    new Paragraph(new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }), new Run(new FieldCode(" TOC ")), new Run(new FieldChar { FieldCharType = FieldCharValues.End })),
                    new Paragraph(new Run(new Break { Type = BreakValues.Page })),
                    new Table(
                        new TableProperties(new TableBorders(new TopBorder { Val = BorderValues.Single })),
                        new TableRow(new TableRowProperties(new TableHeader()), new TableCell(new TableCellProperties(new Shading { Fill = "D9EAF7" }), new Paragraph(new Run(new Text("表头"))))),
                        new TableRow(new TableCell(new Paragraph(new Run(new Text("内容")))))),
                    new SectionProperties(
                        new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(headerPart) },
                        new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) },
                        new TitlePage(),
                        new PageNumberType { Start = 1, Format = NumberFormatValues.Decimal },
                        new PageSize { Width = 11906U, Height = 16838U },
                        new PageMargin { Top = 1440, Bottom = 1440, Left = 1440U, Right = 1440U }));
                main.Document = new Document(body);
                main.Document.Save();
            }
        }
    }
}
