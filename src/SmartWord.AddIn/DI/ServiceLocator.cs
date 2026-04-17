using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Serilog;
using SmartWord.Application.Context;
using SmartWord.Application.Orchestration;
using SmartWord.Application.Pipeline;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Tools;
using SmartWord.AddIn.TaskPane;
using SmartWord.Core.Interfaces;
using SmartWord.Infrastructure.Configuration;
using SmartWord.Infrastructure.LlmClients;
using SmartWord.Infrastructure.Persistence;
using SmartWord.OfficeIntegration.Scripting;
using SmartWord.OfficeIntegration.Tools;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.AddIn.DI
{
    /// <summary>
    /// 负责在插件启动时组装各层依赖。
    /// </summary>
    public static class ServiceLocator
    {
        private static readonly object SettingsSyncRoot = new object();
        private static ServiceProvider _serviceProvider;
        private static WebViewConfirmationChannel _confirmationChannel;

        public static void Initialize(Microsoft.Office.Interop.Word.Application wordApplication)
        {
            Dispose();

            var services = new ServiceCollection();
            // 必须在 AddIn 启动的 UI 线程上提前创建包装器，
            // 避免首次在后台线程解析单例时把错误线程记录为 Word COM 所属线程。
            var wordApplicationWrapper = new WordApplicationWrapper(wordApplication);
            _confirmationChannel = new WebViewConfirmationChannel();
            services.AddSingleton(wordApplication);
            services.AddSingleton(wordApplicationWrapper);
            services.AddSingleton<IUndoScopeFactory>(wordApplicationWrapper);
            services.AddSingleton(_confirmationChannel);
            services.AddSingleton<IConfirmationChannel>(_confirmationChannel);
            services.AddSingleton<ScriptSecurityValidator>();
            services.AddSingleton<CSharpScriptExecutor>();
            services.AddSingleton(provider => LoadSmartWordSettings());
            services.AddSingleton(provider => CreateLlmClientOptions(
                provider.GetRequiredService<SmartWordSettings>()));
            services.AddSingleton<ILlmClient, OpenAiCompatibleClient>();
            services.AddSingleton<IConversationStore, InMemoryConversationStore>();
            services.AddSingleton<IContextHydrator, ContextHydrator>();
            services.AddSingleton<IToolRegistry>(provider =>
            {
                var registry = new ToolRegistry();
                var wordWrapper = provider.GetRequiredService<WordApplicationWrapper>();
                registry.Register(new ProbeDocumentTool(wordWrapper));
                registry.Register(new ReadSectionTool(wordWrapper));
                registry.Register(new GrepDocumentTool(wordWrapper));
                registry.Register(new GetSelectionContextTool(wordWrapper));
                registry.Register(new ReadTableTool(wordWrapper));
                registry.Register(new ReadAnnotationsTool(wordWrapper));
                registry.Register(new ReadScriptTool(
                    wordWrapper,
                    provider.GetRequiredService<CSharpScriptExecutor>(),
                    provider.GetRequiredService<ScriptSecurityValidator>()));
                registry.Register(new VerifyScriptTool(
                    wordWrapper,
                    provider.GetRequiredService<CSharpScriptExecutor>(),
                    provider.GetRequiredService<ScriptSecurityValidator>()));
                registry.Register(new PatchRangeTool(wordWrapper));
                registry.Register(new ExecuteScriptTool(
                    wordWrapper,
                    provider.GetRequiredService<CSharpScriptExecutor>(),
                    provider.GetRequiredService<ScriptSecurityValidator>()));
                return registry;
            });
            services.AddSingleton<PermissionGuard>();
            services.AddSingleton<ConversationCompressor>();
            services.AddSingleton(provider => new SystemPromptBuilder(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Prompts")));
            services.AddSingleton<IAgentOrchestrator>(provider => new AgentOrchestrator(
                provider.GetRequiredService<ILlmClient>(),
                provider.GetRequiredService<IContextHydrator>(),
                provider.GetRequiredService<IConversationStore>(),
                provider.GetRequiredService<SystemPromptBuilder>(),
                provider.GetRequiredService<IToolRegistry>(),
                provider.GetRequiredService<PermissionGuard>(),
                provider.GetRequiredService<IConfirmationChannel>(),
                provider.GetRequiredService<IUndoScopeFactory>(),
                provider.GetRequiredService<ConversationCompressor>()));
            services.AddSingleton<StreamingResponseHandler>();

            _serviceProvider = services.BuildServiceProvider();
        }

        public static T GetService<T>()
            where T : class
        {
            return _serviceProvider?.GetService<T>();
        }

        public static T GetRequiredService<T>()
            where T : class
        {
            if (_serviceProvider == null)
            {
                throw new InvalidOperationException("ServiceLocator 尚未初始化。");
            }

            return _serviceProvider.GetRequiredService<T>();
        }

        public static SmartWordSettings GetCurrentSettingsSnapshot()
        {
            lock (SettingsSyncRoot)
            {
                var persistedSettings = GetRequiredService<SmartWordSettings>();
                var llmOptions = GetRequiredService<LlmClientOptions>();

                return new SmartWordSettings
                {
                    ApiBaseUrl = llmOptions.BaseUrl,
                    BaseUrl = llmOptions.BaseUrl,
                    ApiKey = llmOptions.ApiKey,
                    BaseUrlHeavy = llmOptions.BaseUrlHeavy,
                    ApiKeyHeavy = llmOptions.ApiKeyHeavy,
                    BaseUrlLight = llmOptions.BaseUrlLight,
                    ApiKeyLight = llmOptions.ApiKeyLight,
                    LightModel = llmOptions.LightModel,
                    HeavyModel = llmOptions.HeavyModel,
                    RequireConfirmationForScripts = persistedSettings.RequireConfirmationForScripts,
                    CustomInstructions = persistedSettings.CustomInstructions
                };
            }
        }

        public static SmartWordSettings SaveSettings(SmartWordSettings incomingSettings)
        {
            if (incomingSettings == null)
            {
                throw new ArgumentNullException(nameof(incomingSettings));
            }

            lock (SettingsSyncRoot)
            {
                var normalizedSettings = NormalizeSettings(incomingSettings);
                var settingsPath = GetSettingsPath();
                var settingsDirectory = Path.GetDirectoryName(settingsPath);
                if (!string.IsNullOrWhiteSpace(settingsDirectory))
                {
                    Directory.CreateDirectory(settingsDirectory);
                }

                File.WriteAllText(
                    settingsPath,
                    JsonConvert.SerializeObject(normalizedSettings, Formatting.Indented));

                var persistedSettings = GetRequiredService<SmartWordSettings>();
                ApplySettings(normalizedSettings, persistedSettings);

                var llmOptions = GetRequiredService<LlmClientOptions>();
                ApplySettingsToLlmOptions(normalizedSettings, llmOptions);

                Log.Information("SmartWord 设置已保存到 {SettingsPath}", settingsPath);
                return CloneSettings(persistedSettings);
            }
        }

        public static void Dispose()
        {
            _confirmationChannel?.DetachBridge();
            _confirmationChannel = null;
            _serviceProvider?.Dispose();
            _serviceProvider = null;
        }

        public static void AttachTaskPaneBridge(SmartWordBridge bridge)
        {
            _confirmationChannel?.AttachBridge(bridge);
        }

        private static SmartWordSettings LoadSmartWordSettings()
        {
            var settings = new SmartWordSettings();
            var settingsPath = GetSettingsPath();

            if (File.Exists(settingsPath))
            {
                try
                {
                    settings = JsonConvert.DeserializeObject<SmartWordSettings>(File.ReadAllText(settingsPath)) ?? settings;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "读取 settings.json 失败，将继续使用默认配置。");
                }
            }

            return NormalizeSettings(settings);
        }

        private static LlmClientOptions CreateLlmClientOptions(SmartWordSettings settings)
        {
            var options = new LlmClientOptions
            {
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                BaseUrlHeavy = settings.BaseUrlHeavy,
                ApiKeyHeavy = settings.ApiKeyHeavy,
                BaseUrlLight = settings.BaseUrlLight,
                ApiKeyLight = settings.ApiKeyLight,
                LightModel = settings.LightModel,
                HeavyModel = settings.HeavyModel
            };

            ApplyEnvironmentOverrides(options);
            return options;
        }

        private static SmartWordSettings NormalizeSettings(SmartWordSettings settings)
        {
            var normalized = CloneSettings(settings ?? new SmartWordSettings());
            if (string.IsNullOrWhiteSpace(normalized.BaseUrl))
            {
                normalized.BaseUrl = string.IsNullOrWhiteSpace(normalized.ApiBaseUrl)
                    ? "https://api.openai.com/v1"
                    : normalized.ApiBaseUrl;
            }

            normalized.ApiBaseUrl = normalized.BaseUrl;
            normalized.LightModel = string.IsNullOrWhiteSpace(normalized.LightModel)
                ? "gpt-4.1-mini"
                : normalized.LightModel;
            normalized.HeavyModel = string.IsNullOrWhiteSpace(normalized.HeavyModel)
                ? "gpt-4.1"
                : normalized.HeavyModel;
            return normalized;
        }

        private static SmartWordSettings CloneSettings(SmartWordSettings settings)
        {
            return new SmartWordSettings
            {
                ApiBaseUrl = settings.ApiBaseUrl,
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                BaseUrlHeavy = settings.BaseUrlHeavy,
                ApiKeyHeavy = settings.ApiKeyHeavy,
                BaseUrlLight = settings.BaseUrlLight,
                ApiKeyLight = settings.ApiKeyLight,
                LightModel = settings.LightModel,
                HeavyModel = settings.HeavyModel,
                RequireConfirmationForScripts = settings.RequireConfirmationForScripts,
                CustomInstructions = settings.CustomInstructions
            };
        }

        private static void ApplySettings(SmartWordSettings source, SmartWordSettings target)
        {
            target.ApiBaseUrl = source.ApiBaseUrl;
            target.BaseUrl = source.BaseUrl;
            target.ApiKey = source.ApiKey;
            target.BaseUrlHeavy = source.BaseUrlHeavy;
            target.ApiKeyHeavy = source.ApiKeyHeavy;
            target.BaseUrlLight = source.BaseUrlLight;
            target.ApiKeyLight = source.ApiKeyLight;
            target.LightModel = source.LightModel;
            target.HeavyModel = source.HeavyModel;
            target.RequireConfirmationForScripts = source.RequireConfirmationForScripts;
            target.CustomInstructions = source.CustomInstructions;
        }

        private static void ApplySettingsToLlmOptions(SmartWordSettings settings, LlmClientOptions options)
        {
            options.BaseUrl = settings.BaseUrl;
            options.ApiKey = settings.ApiKey;
            options.BaseUrlHeavy = settings.BaseUrlHeavy;
            options.ApiKeyHeavy = settings.ApiKeyHeavy;
            options.BaseUrlLight = settings.BaseUrlLight;
            options.ApiKeyLight = settings.ApiKeyLight;
            options.LightModel = settings.LightModel;
            options.HeavyModel = settings.HeavyModel;
        }

        private static string GetSettingsPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SmartWord",
                "settings.json");
        }

        private static void ApplyEnvironmentOverrides(LlmClientOptions options)
        {
            options.ApiKey = OverrideString(options.ApiKey, "SMARTWORD_API_KEY");
            options.BaseUrl = OverrideString(options.BaseUrl, "SMARTWORD_BASE_URL");
            options.ApiKeyHeavy = OverrideString(options.ApiKeyHeavy, "SMARTWORD_API_KEY_HEAVY");
            options.BaseUrlHeavy = OverrideString(options.BaseUrlHeavy, "SMARTWORD_BASE_URL_HEAVY");
            options.ApiKeyLight = OverrideString(options.ApiKeyLight, "SMARTWORD_API_KEY_LIGHT");
            options.BaseUrlLight = OverrideString(options.BaseUrlLight, "SMARTWORD_BASE_URL_LIGHT");
            options.HeavyModel = OverrideString(options.HeavyModel, "SMARTWORD_HEAVY_MODEL");
            options.LightModel = OverrideString(options.LightModel, "SMARTWORD_LIGHT_MODEL");
            options.TimeoutSeconds = OverrideInt(options.TimeoutSeconds, "SMARTWORD_TIMEOUT_SECONDS");
            options.MaxRetries = OverrideInt(options.MaxRetries, "SMARTWORD_MAX_RETRIES");
            options.RetryDelayMs = OverrideInt(options.RetryDelayMs, "SMARTWORD_RETRY_DELAY_MS");
        }

        private static string OverrideString(string target, string environmentVariableName)
        {
            var value = Environment.GetEnvironmentVariable(environmentVariableName);
            return string.IsNullOrWhiteSpace(value) ? target : value;
        }

        private static int OverrideInt(int target, string environmentVariableName)
        {
            var value = Environment.GetEnvironmentVariable(environmentVariableName);
            return int.TryParse(value, out var parsedValue) ? parsedValue : target;
        }
    }
}
