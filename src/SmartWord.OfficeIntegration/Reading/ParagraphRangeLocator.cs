using System;
using System.Collections.Generic;
using SmartWord.OfficeIntegration.Models;

namespace SmartWord.OfficeIntegration.Reading
{
    /// <summary>
    /// 根据 Word Range 的位置定位所在段落。
    /// </summary>
    public static class ParagraphRangeLocator
    {
        public static int LocateParagraphIndex(IReadOnlyList<ParagraphRangeBounds> paragraphRanges, int position)
        {
            if (paragraphRanges == null || paragraphRanges.Count == 0 || position < 0)
            {
                return -1;
            }

            var previous = paragraphRanges[0];
            foreach (var paragraphRange in paragraphRanges)
            {
                if (position < paragraphRange.Start)
                {
                    return previous.Index;
                }

                if (position >= paragraphRange.Start && position <= Math.Max(paragraphRange.Start, paragraphRange.End))
                {
                    return paragraphRange.Index;
                }

                previous = paragraphRange;
            }

            return previous.Index;
        }
    }
}
