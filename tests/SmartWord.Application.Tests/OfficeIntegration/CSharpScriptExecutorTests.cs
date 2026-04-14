using System.Threading;
using System.Threading.Tasks;
using SmartWord.OfficeIntegration.Scripting;
using Xunit;

namespace SmartWord.Application.Tests.OfficeIntegration
{
    public class CSharpScriptExecutorTests
    {
        [Fact]
        public async Task ExecuteAsync_AppAndDocAliases_AreAvailable()
        {
            var executor = new CSharpScriptExecutor();
            var globals = new ScriptGlobals
            {
                App = new FakeWordApplication
                {
                    ActiveDocument = new FakeDocument()
                },
                Doc = new FakeDocument()
            };

            var result = await executor.ExecuteAsync(
                "Write(app.ActiveDocument != null ? \"ok\" : \"bad\"); return doc != null;",
                globals,
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Contains("ok", result.Output);
        }

        public sealed class FakeWordApplication
        {
            public object ActiveDocument { get; set; }
        }

        public sealed class FakeDocument
        {
        }
    }
}
