using System.Reflection;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class VerifyScriptToolContractTests
    {
        [Fact]
        public void ExecuteScriptTool_RuntimeErrorHelper_仍保持内部可复用()
        {
            var method = typeof(SmartWord.OfficeIntegration.Tools.ExecuteScriptTool).GetMethod(
                "BuildScriptRuntimeErrorMessage",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.NotNull(method);
        }
    }
}
