using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SmartWord.OfficeIntegration.Tests.Infrastructure;
using Xunit;

namespace SmartWord.OfficeIntegration.Tests
{
    [Collection(RealWordCollection.Name)]
    public sealed class WordApplicationLifecycleTests
    {
        [WordIntegrationFact]
        public async Task Word会话_打开保存并关闭文档_测试拥有的进程退出()
        {
            var ownedProcessId = 0;

            await StaWordTestHost.RunAsync(async session =>
            {
                ownedProcessId = session.OwnedProcessId;
                var fixturePath = await session.CreateBasicFixtureAsync();
                Assert.True(File.Exists(fixturePath));

                await session.OpenDocumentAsync(fixturePath);
                var activePath = await session.WordWrapper.GetActiveDocumentPath();
                var text = await session.ReadActiveDocumentTextAsync();

                Assert.Equal(fixturePath, activePath, ignoreCase: true);
                Assert.Contains("第一段内容", text);
                await session.SaveActiveDocumentAsync();
            });

            Assert.False(IsProcessRunning(ownedProcessId));
        }

        private static bool IsProcessRunning(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
