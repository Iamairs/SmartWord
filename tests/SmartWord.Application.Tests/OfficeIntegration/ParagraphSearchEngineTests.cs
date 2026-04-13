using System.Collections.Generic;
using SmartWord.OfficeIntegration.Models;
using SmartWord.OfficeIntegration.Reading;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class ParagraphSearchEngineTests
    {
        [Fact]
        public void Search_同一段多个关键词_返回全部命中位置()
        {
            var searchEngine = new ParagraphSearchEngine();
            var paragraphs = new List<ParagraphSnapshot>
            {
                new ParagraphSnapshot
                {
                    Index = 12,
                    Text = "违约金条款中的违约金需要单独说明。"
                }
            };

            var result = searchEngine.Search(paragraphs, "违约金", false, 10);

            Assert.Equal(1, result.TotalHitParagraphs);
            Assert.Equal(2, result.TotalMatches);
            Assert.Single(result.Results);
            Assert.Equal(2, result.Results[0].Matches.Count);
            Assert.Equal(0, result.Results[0].Matches[0].Start);
        }

        [Fact]
        public void Search_非法正则_返回明确错误()
        {
            var searchEngine = new ParagraphSearchEngine();
            var paragraphs = new List<ParagraphSnapshot>
            {
                new ParagraphSnapshot
                {
                    Index = 1,
                    Text = "示例文本"
                }
            };

            var result = searchEngine.Search(paragraphs, "([", true, 10);

            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void Search_命中段落超过限制_保留总命中统计并标记截断()
        {
            var searchEngine = new ParagraphSearchEngine();
            var paragraphs = new List<ParagraphSnapshot>
            {
                new ParagraphSnapshot { Index = 0, Text = "违约金 A" },
                new ParagraphSnapshot { Index = 1, Text = "违约金 B" }
            };

            var result = searchEngine.Search(paragraphs, "违约金", false, 1);

            Assert.Equal(2, result.TotalHitParagraphs);
            Assert.Equal(2, result.TotalMatches);
            Assert.True(result.IsTruncated);
            Assert.Single(result.Results);
        }
    }
}
