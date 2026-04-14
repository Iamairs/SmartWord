using SmartWord.OfficeIntegration.Tools;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class VerifyChangeEvaluatorTests
    {
        [Fact]
        public void Evaluate_TextContainsMismatch_ReturnsHint()
        {
            var result = VerifyChangeEvaluator.Evaluate(
                new VerifyChangeCheck
                {
                    CheckIndex = 0,
                    Type = "text_contains",
                    ParagraphIndex = 2,
                    Expected = "目标文本"
                },
                true,
                "当前内容",
                "Normal");

            Assert.False(result.Passed);
            Assert.Contains("文本未包含预期内容", result.Hint);
        }

        [Fact]
        public void Evaluate_ParagraphExistsExpectedFalse_UsesExistenceFlag()
        {
            var result = VerifyChangeEvaluator.Evaluate(
                new VerifyChangeCheck
                {
                    CheckIndex = 1,
                    Type = "paragraph_exists",
                    ParagraphIndex = 4,
                    ShouldExist = false
                },
                false,
                string.Empty,
                string.Empty);

            Assert.True(result.Passed);
            Assert.Equal("false", result.Actual);
            Assert.Equal("false", result.Expected);
        }

        [Fact]
        public void Evaluate_UnknownType_ReturnsFailure()
        {
            var result = VerifyChangeEvaluator.Evaluate(
                new VerifyChangeCheck
                {
                    CheckIndex = 2,
                    Type = "unsupported",
                    ParagraphIndex = 0
                },
                true,
                "abc",
                "Normal");

            Assert.False(result.Passed);
            Assert.Contains("未知的验证类型", result.Hint);
        }
    }
}
