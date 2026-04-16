using System;
using SmartWord.Infrastructure.LlmClients;
using Xunit;

namespace SmartWord.Application.Tests.Infrastructure
{
    /// <summary>
    /// 验证 LLM 流式阶段超时配置遵循可配置项，而不是硬编码常量。
    /// </summary>
    public sealed class OpenAiCompatibleClientTimeoutTests
    {
        [Theory]
        [InlineData(120, 120)]
        [InlineData(30, 30)]
        [InlineData(0, 120)]
        [InlineData(-5, 120)]
        [InlineData(5, 15)]
        public void ResolveStreamPhaseTimeout_UsesConfiguredSecondsWithReasonableFloor(
            int configuredTimeoutSeconds,
            int expectedTimeoutSeconds)
        {
            var timeout = OpenAiCompatibleClient.ResolveStreamPhaseTimeout(configuredTimeoutSeconds);

            Assert.Equal(TimeSpan.FromSeconds(expectedTimeoutSeconds), timeout);
        }
    }
}
