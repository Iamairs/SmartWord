using SmartWord.OfficeIntegration.Scripting;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class ScriptSecurityValidatorTests
    {
        [Fact]
        public void Validate_ForbiddenUsingNamespace_ReturnsInvalid()
        {
            var validator = new ScriptSecurityValidator();

            var result = validator.Validate("using System.IO; return 1;");

            Assert.False(result.IsValid);
            Assert.Contains("受限", result.Message);
        }

        [Fact]
        public void Validate_ForbiddenIdentifier_ReturnsInvalid()
        {
            var validator = new ScriptSecurityValidator();

            var result = validator.Validate("return File.ReadAllText(\"a.txt\");");

            Assert.False(result.IsValid);
            Assert.Contains("File", result.Message);
        }

        [Fact]
        public void Validate_ReadOnlyScriptWithMutationMethod_ReturnsInvalid()
        {
            var validator = new ScriptSecurityValidator();

            var result = validator.Validate("doc.Paragraphs[1].Range.InsertAfter(\"x\");", ScriptValidationMode.ReadOnly);

            Assert.False(result.IsValid);
            Assert.Contains("只读", result.Message);
        }

        [Fact]
        public void Validate_ReadOnlyScriptWithMemberAssignment_ReturnsInvalid()
        {
            var validator = new ScriptSecurityValidator();

            var result = validator.Validate("doc.Paragraphs[1].Range.Text = \"x\";", ScriptValidationMode.ReadOnly);

            Assert.False(result.IsValid);
            Assert.Contains("赋值", result.Message);
        }

        [Fact]
        public void Validate_SafeScript_ReturnsValid()
        {
            var validator = new ScriptSecurityValidator();

            var result = validator.Validate("Write(\"ok\"); return ActiveDoc != null;");

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validate_ReadOnlySafeScript_ReturnsValid()
        {
            var validator = new ScriptSecurityValidator();

            var result = validator.Validate("var count = doc == null ? 0 : doc.Paragraphs.Count; return new { all_passed = true, results = new object[0], count = count };", ScriptValidationMode.ReadOnly);

            Assert.True(result.IsValid);
        }
    }
}
