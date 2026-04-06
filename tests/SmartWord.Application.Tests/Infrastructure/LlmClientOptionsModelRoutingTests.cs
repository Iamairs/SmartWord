using SmartWord.Core.Enums;
using SmartWord.Infrastructure.LlmClients;
using Xunit;

namespace SmartWord.Application.Tests.Infrastructure
{
    public class LlmClientOptionsModelRoutingTests
    {
        [Fact]
        public void GetModelCapability_DeepSeekV32_RequiresReasoningReplay()
        {
            var options = new LlmClientOptions();

            var capability = options.GetModelCapability("deepseek-ai/DeepSeek-V3.2");

            Assert.True(capability.SupportsToolCalling);
            Assert.True(capability.RequiresReasoningContentReplay);
        }

        [Fact]
        public void ResolveModelRoute_AskModeLightModelNoToolCalling_FallsBackToHeavyModel()
        {
            var options = new LlmClientOptions
            {
                LightModel = "deepseek-ai/DeepSeek-V3.2-Speciale",
                HeavyModel = "deepseek-ai/DeepSeek-V3.2"
            };

            var decision = options.ResolveModelRoute(AgentMode.Ask);

            Assert.Equal("deepseek-ai/DeepSeek-V3.2", decision.SelectedModel);
            Assert.True(decision.EnableToolCalling);
            Assert.True(decision.UsedFallbackModel);
        }

        [Fact]
        public void ResolveModelRoute_WhenNoConfiguredModelSupportsToolCalling_DisablesToolCalling()
        {
            var options = new LlmClientOptions
            {
                LightModel = "deepseek-ai/DeepSeek-V3.2-Speciale",
                HeavyModel = "custom-speciale-model"
            };

            var decision = options.ResolveModelRoute(AgentMode.Ask);

            Assert.Equal("deepseek-ai/DeepSeek-V3.2-Speciale", decision.SelectedModel);
            Assert.False(decision.EnableToolCalling);
            Assert.False(decision.UsedFallbackModel);
        }
    }
}
