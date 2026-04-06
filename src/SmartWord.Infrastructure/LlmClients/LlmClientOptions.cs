using System;
using SmartWord.Core.Enums;

namespace SmartWord.Infrastructure.LlmClients
{
    /// <summary>
    /// 存放 OpenAI 兼容接口所需的基础配置，并提供基于模型能力的路由能力。
    /// </summary>
    public class LlmClientOptions
    {
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";

        public string ApiKey { get; set; } = string.Empty;

        public string BaseUrlHeavy { get; set; } = string.Empty;

        public string ApiKeyHeavy { get; set; } = string.Empty;

        public string BaseUrlLight { get; set; } = string.Empty;

        public string ApiKeyLight { get; set; } = string.Empty;

        public string LightModel { get; set; } = "gpt-4.1-mini";

        public string HeavyModel { get; set; } = "gpt-4.1";

        public int TimeoutSeconds { get; set; } = 120;

        public int MaxRetries { get; set; } = 3;

        public int RetryDelayMs { get; set; } = 1000;

        public string GetBaseUrlForModel(string model)
        {
            if (IsHeavyModel(model))
            {
                return string.IsNullOrWhiteSpace(BaseUrlHeavy) ? BaseUrl : BaseUrlHeavy;
            }

            if (IsLightModel(model))
            {
                return string.IsNullOrWhiteSpace(BaseUrlLight) ? BaseUrl : BaseUrlLight;
            }

            return BaseUrl;
        }

        public string GetApiKeyForModel(string model)
        {
            if (IsHeavyModel(model))
            {
                return string.IsNullOrWhiteSpace(ApiKeyHeavy) ? ApiKey : ApiKeyHeavy;
            }

            if (IsLightModel(model))
            {
                return string.IsNullOrWhiteSpace(ApiKeyLight) ? ApiKey : ApiKeyLight;
            }

            return ApiKey;
        }

        public ModelCapability GetModelCapability(string model)
        {
            var normalized = NormalizeModelName(model);
            var capability = new ModelCapability
            {
                Model = model ?? string.Empty,
                SupportsToolCalling = true,
                RequiresReasoningContentReplay = false,
                CapabilitySource = "default"
            };

            if (string.IsNullOrWhiteSpace(normalized))
            {
                capability.SupportsToolCalling = false;
                capability.CapabilitySource = "empty_model";
                return capability;
            }

            // DeepSeek V3.2 / GLM-4.7 在 SiliconFlow 的工具链路中要求回放 reasoning_content。
            if (normalized.Contains("deepseek-v3.2") || normalized.Contains("glm-4.7"))
            {
                capability.RequiresReasoningContentReplay = true;
                capability.CapabilitySource = "interleaved_thinking";
            }

            // 目前已知 DeepSeek-V3.2-Speciale 不支持工具调用。
            if (normalized.Contains("speciale"))
            {
                capability.SupportsToolCalling = false;
                capability.CapabilitySource = "known_no_tool_support";
            }

            return capability;
        }

        public ModelRoutingDecision ResolveModelRoute(AgentMode mode)
        {
            var preferredModel = ResolvePreferredModel(mode);
            var alternateModel = ResolveAlternateModel(mode);
            var preferredCapability = GetModelCapability(preferredModel);
            var alternateCapability = GetModelCapability(alternateModel);
            var requiresToolCalling = ModeRequiresToolCalling(mode);

            if (!requiresToolCalling)
            {
                return new ModelRoutingDecision
                {
                    SelectedModel = preferredModel,
                    EnableToolCalling = preferredCapability.SupportsToolCalling,
                    SelectedCapability = preferredCapability,
                    RoutingMessage = "当前模式不强依赖工具调用，保持首选模型。"
                };
            }

            if (preferredCapability.SupportsToolCalling)
            {
                return new ModelRoutingDecision
                {
                    SelectedModel = preferredModel,
                    EnableToolCalling = true,
                    SelectedCapability = preferredCapability,
                    RoutingMessage = preferredCapability.RequiresReasoningContentReplay
                        ? "首选模型支持工具调用，并启用 reasoning_content 回放。"
                        : "首选模型支持工具调用，继续按首选模型执行。"
                };
            }

            if (!string.IsNullOrWhiteSpace(alternateModel)
                && !string.Equals(alternateModel, preferredModel, StringComparison.OrdinalIgnoreCase)
                && alternateCapability.SupportsToolCalling)
            {
                return new ModelRoutingDecision
                {
                    SelectedModel = alternateModel,
                    EnableToolCalling = true,
                    SelectedCapability = alternateCapability,
                    UsedFallbackModel = true,
                    RoutingMessage =
                        $"首选模型 {preferredModel} 不支持工具调用，已自动切换到 {alternateModel}。"
                };
            }

            return new ModelRoutingDecision
            {
                SelectedModel = preferredModel,
                EnableToolCalling = false,
                SelectedCapability = preferredCapability,
                RoutingMessage = "当前配置的模型均不支持工具调用，已降级为纯文本回答模式。"
            };
        }

        private string ResolvePreferredModel(AgentMode mode)
        {
            switch (mode)
            {
                case AgentMode.Ask:
                    return LightModel;
                case AgentMode.Plan:
                case AgentMode.Agent:
                default:
                    return HeavyModel;
            }
        }

        private string ResolveAlternateModel(AgentMode mode)
        {
            switch (mode)
            {
                case AgentMode.Ask:
                    return HeavyModel;
                case AgentMode.Plan:
                case AgentMode.Agent:
                default:
                    return LightModel;
            }
        }

        private static bool ModeRequiresToolCalling(AgentMode mode)
        {
            switch (mode)
            {
                case AgentMode.Ask:
                case AgentMode.Plan:
                case AgentMode.Agent:
                    return true;
                default:
                    return false;
            }
        }

        private bool IsHeavyModel(string model)
        {
            return !string.IsNullOrWhiteSpace(model)
                && string.Equals(model, HeavyModel, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsLightModel(string model)
        {
            return !string.IsNullOrWhiteSpace(model)
                && string.Equals(model, LightModel, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeModelName(string model)
        {
            return string.IsNullOrWhiteSpace(model)
                ? string.Empty
                : model.Trim().ToLowerInvariant();
        }
    }

    public sealed class ModelCapability
    {
        public string Model { get; set; } = string.Empty;

        public bool SupportsToolCalling { get; set; }

        public bool RequiresReasoningContentReplay { get; set; }

        public string CapabilitySource { get; set; } = string.Empty;
    }

    public sealed class ModelRoutingDecision
    {
        public string SelectedModel { get; set; } = string.Empty;

        public bool EnableToolCalling { get; set; }

        public bool UsedFallbackModel { get; set; }

        public string RoutingMessage { get; set; } = string.Empty;

        public ModelCapability SelectedCapability { get; set; } = new ModelCapability();
    }
}
