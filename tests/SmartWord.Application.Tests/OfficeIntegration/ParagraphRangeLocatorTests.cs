using System.Collections.Generic;
using SmartWord.OfficeIntegration.Models;
using SmartWord.OfficeIntegration.Reading;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class ParagraphRangeLocatorTests
    {
        [Fact]
        public void LocateParagraphIndex_光标位于段中_返回当前段索引()
        {
            var paragraphRanges = BuildParagraphRanges();

            var paragraphIndex = ParagraphRangeLocator.LocateParagraphIndex(paragraphRanges, 15);

            Assert.Equal(1, paragraphIndex);
        }

        [Fact]
        public void LocateParagraphIndex_光标位于段尾边界_仍返回当前段索引()
        {
            var paragraphRanges = BuildParagraphRanges();

            var paragraphIndex = ParagraphRangeLocator.LocateParagraphIndex(paragraphRanges, 10);

            Assert.Equal(0, paragraphIndex);
        }

        [Fact]
        public void LocateParagraphIndex_超出最后一段_返回最后一段索引()
        {
            var paragraphRanges = BuildParagraphRanges();

            var paragraphIndex = ParagraphRangeLocator.LocateParagraphIndex(paragraphRanges, 99);

            Assert.Equal(2, paragraphIndex);
        }

        private static List<ParagraphRangeBounds> BuildParagraphRanges()
        {
            return new List<ParagraphRangeBounds>
            {
                new ParagraphRangeBounds { Index = 0, Start = 0, End = 10 },
                new ParagraphRangeBounds { Index = 1, Start = 11, End = 20 },
                new ParagraphRangeBounds { Index = 2, Start = 21, End = 30 }
            };
        }
    }
}
