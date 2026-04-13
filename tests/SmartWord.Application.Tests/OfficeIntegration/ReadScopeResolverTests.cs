using System.Collections.Generic;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.Models;
using SmartWord.OfficeIntegration.Reading;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class ReadScopeResolverTests
    {
        [Fact]
        public void Resolve_按标题精确匹配_返回章节范围()
        {
            var resolver = new ReadScopeResolver();
            var diagnostics = new ReadDiagnostics();

            var result = resolver.Resolve(
                new ReadScope
                {
                    Heading = "第三章 违约责任",
                    IncludeSubsections = true
                },
                100,
                BuildHeadings(),
                50,
                new SelectionSnapshot(),
                diagnostics);

            Assert.Equal(40, result.FromParagraph);
            Assert.Equal(69, result.ToParagraph);
            Assert.Equal("第三章 违约责任", result.HeadingText);
            Assert.False(diagnostics.HasWarnings);
        }

        [Fact]
        public void Resolve_标题模糊匹配_返回Warning()
        {
            var resolver = new ReadScopeResolver();
            var diagnostics = new ReadDiagnostics();

            var result = resolver.Resolve(
                new ReadScope
                {
                    Heading = "违约责任"
                },
                100,
                BuildHeadings(),
                50,
                new SelectionSnapshot(),
                diagnostics);

            Assert.Equal(40, result.FromParagraph);
            Assert.True(diagnostics.HasWarnings);
        }

        [Fact]
        public void Resolve_段落范围越界_自动裁剪并给出Warning()
        {
            var resolver = new ReadScopeResolver();
            var diagnostics = new ReadDiagnostics();

            var result = resolver.Resolve(
                new ReadScope
                {
                    FromParagraph = -5,
                    ToParagraph = 999
                },
                20,
                BuildHeadings(),
                0,
                new SelectionSnapshot(),
                diagnostics);

            Assert.Equal(0, result.FromParagraph);
            Assert.Equal(19, result.ToParagraph);
            Assert.True(diagnostics.HasWarnings);
        }

        [Fact]
        public void Resolve_仅按选区读取且无显式选区_回退到光标段落()
        {
            var resolver = new ReadScopeResolver();
            var diagnostics = new ReadDiagnostics();

            var result = resolver.Resolve(
                new ReadScope
                {
                    SelectionOnly = true
                },
                50,
                BuildHeadings(),
                18,
                new SelectionSnapshot
                {
                    HasSelection = false,
                    ParagraphIndex = 18,
                    StartParagraphIndex = 18,
                    EndParagraphIndex = 18
                },
                diagnostics);

            Assert.Equal(18, result.FromParagraph);
            Assert.Equal(18, result.ToParagraph);
            Assert.True(diagnostics.HasWarnings);
        }

        private static List<DocumentHeading> BuildHeadings()
        {
            return new List<DocumentHeading>
            {
                new DocumentHeading { Level = 1, Text = "第一章 总则", ParagraphIndex = 0 },
                new DocumentHeading { Level = 1, Text = "第三章 违约责任", ParagraphIndex = 40 },
                new DocumentHeading { Level = 2, Text = "第四十五条 违约责任说明", ParagraphIndex = 45 },
                new DocumentHeading { Level = 1, Text = "第四章 争议解决", ParagraphIndex = 70 }
            };
        }
    }
}
