using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Serilog;
using SmartWord.Application.Context;
using SmartWord.Application.Orchestration;
using SmartWord.Application.Pipeline;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Skills;
using SmartWord.Application.Todo;
using SmartWord.Application.Tools;
using SmartWord.AddIn.TaskPane;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Telemetry;
using SmartWord.Infrastructure.Configuration;
using SmartWord.Infrastructure.LlmClients;
using SmartWord.Infrastructure.Persistence;
using SmartWord.Infrastructure.Skills;
using SmartWord.Infrastructure.Telemetry;
using SmartWord.OfficeIntegration.SkillScripts;
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
        private const string MaskedSecretValue = "********";
        private static readonly object SettingsSyncRoot = new object();
        private static ServiceProvider _serviceProvider;
        private static WebViewConfirmationChannel _confirmationChannel;
        private static WebViewQuestionChannel _questionChannel;
        private static WebViewTodoRecoveryChannel _todoRecoveryChannel;

        public static void Initialize(Microsoft.Office.Interop.Word.Application wordApplication)
        {
            Dispose();

            var services = new ServiceCollection();
            // 必须在 AddIn 启动的 UI 线程上提前创建包装器，
            // 避免首次在后台线程解析单例时把错误线程记录为 Word COM 所属线程。
            var wordApplicationWrapper = new WordApplicationWrapper(wordApplication);
            _confirmationChannel = new WebViewConfirmationChannel();
            _questionChannel = new WebViewQuestionChannel();
            _todoRecoveryChannel = new WebViewTodoRecoveryChannel();
            services.AddSingleton(wordApplication);
            services.AddSingleton(wordApplicationWrapper);
            services.AddSingleton<IUndoScopeFactory>(wordApplicationWrapper);
            services.AddSingleton(_confirmationChannel);
            services.AddSingleton<IConfirmationChannel>(_confirmationChannel);
            services.AddSingleton<IToolConfirmationChannel>(_confirmationChannel);
            services.AddSingleton(_questionChannel);
            services.AddSingleton<IQuestionChannel>(_questionChannel);
            services.AddSingleton(_todoRecoveryChannel);
            services.AddSingleton<ITodoRecoveryChannel>(_todoRecoveryChannel);
            services.AddSingleton<ScriptSecurityValidator>();
            services.AddSingleton<CSharpScriptExecutor>();
            services.AddSingleton(provider => LoadSmartWordSettings());
            services.AddSingleton(provider => CreateLlmClientOptions(
                provider.GetRequiredService<SmartWordSettings>()));
            var localTelemetryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SmartWord",
                "telemetry",
                "agent-events.jsonl");
            services.AddSingleton<IAgentTelemetrySink>(_ => new LocalAgentTelemetrySink(localTelemetryPath));
            services.AddSingleton(_ => new LocalSkillTelemetryReader(localTelemetryPath));
            services.AddSingleton<ILlmClient, OpenAiCompatibleClient>();
            services.AddSingleton<SmartWordSqliteDatabase>();
            services.AddSingleton<IConversationStore, SqliteConversationStore>();
            services.AddSingleton<ITaskHistoryStore, SqliteTaskHistoryStore>();
            services.AddSingleton<ITodoStore, JsonTodoStore>();
            services.AddSingleton<ISkillStore, FileSystemSkillStore>();
            services.AddSingleton<ISkillPackageInstaller, FileSystemSkillPackageInstaller>();
            services.AddSingleton<ISkillScriptApprovalStore, FileSkillScriptApprovalStore>();
            services.AddSingleton<ISkillScriptRunner, OutOfProcessSkillScriptRunner>();
            services.AddSingleton<ISkillPromptResolver, SkillPromptResolver>();
            services.AddSingleton<IContextHydrator, ContextHydrator>();
            services.AddSingleton<TodoManager>();
            services.AddSingleton<TodoReminderService>();
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
                registry.Register(new SkillRunScriptTool(
                    provider.GetRequiredService<ISkillStore>(),
                    provider.GetRequiredService<ISkillScriptRunner>()));
                registry.Register(new ReadSkillResourceTool(
                    provider.GetRequiredService<ISkillStore>()));
                registry.Register(new AskUserQuestionTool());
                registry.Register(new TodoReadTool(provider.GetRequiredService<TodoManager>()));
                registry.Register(new TodoWriteTool(provider.GetRequiredService<TodoManager>()));
                return registry;
            });
            services.AddSingleton<PermissionGuard>();
            services.AddSingleton<ConversationCompressor>();
            services.AddSingleton<ContextBudgetPolicy>();
            services.AddSingleton<LightToolResultPruner>();
            services.AddSingleton<OversizedToolResultTruncator>();
            services.AddSingleton<ProgramHardStateBuilder>();
            services.AddSingleton<LlmHistoryCompactor>();
            services.AddSingleton<ContextCompactionService>();
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
                provider.GetRequiredService<ConversationCompressor>(),
                provider.GetRequiredService<IQuestionChannel>(),
                provider.GetRequiredService<ITodoRecoveryChannel>(),
                provider.GetRequiredService<TodoManager>(),
                provider.GetRequiredService<TodoReminderService>(),
                provider.GetRequiredService<ITaskHistoryStore>(),
                provider.GetRequiredService<ISkillPromptResolver>(),
                provider.GetRequiredService<ISkillScriptApprovalStore>(),
                provider.GetRequiredService<ContextCompactionService>(),
                provider.GetRequiredService<IAgentTelemetrySink>()));
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
                return CreateUiSettingsSnapshot(persistedSettings, llmOptions);
            }
        }

        public static LlmClientOptions CreateLlmClientOptionsPreview(SmartWordSettings incomingSettings)
        {
            lock (SettingsSyncRoot)
            {
                var existingSettings = GetRequiredService<SmartWordSettings>();
                var normalizedSettings = NormalizeSettings(incomingSettings, existingSettings);
                return CreateLlmClientOptions(normalizedSettings);
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
                var persistedSettings = GetRequiredService<SmartWordSettings>();
                var normalizedSettings = NormalizeSettings(incomingSettings, persistedSettings);
                var settingsToPersist = CreatePersistedSettings(normalizedSettings);
                var settingsPath = GetSettingsPath();
                var settingsDirectory = Path.GetDirectoryName(settingsPath);
                if (!string.IsNullOrWhiteSpace(settingsDirectory))
                {
                    Directory.CreateDirectory(settingsDirectory);
                }

                File.WriteAllText(
                    settingsPath,
                    JsonConvert.SerializeObject(settingsToPersist, Formatting.Indented));

                ApplySettings(normalizedSettings, persistedSettings);

                var llmOptions = GetRequiredService<LlmClientOptions>();
                ApplySettingsToLlmOptions(normalizedSettings, llmOptions);

                Log.Information("SmartWord 设置已保存到 {SettingsPath}", settingsPath);
                return CreateUiSettingsSnapshot(persistedSettings, llmOptions);
            }
        }

        public static void Dispose()
        {
            _confirmationChannel?.DetachBridge();
            _confirmationChannel = null;
            _questionChannel?.DetachBridge();
            _questionChannel = null;
            _todoRecoveryChannel?.DetachBridge();
            _todoRecoveryChannel = null;
            _serviceProvider?.Dispose();
            _serviceProvider = null;
        }

        public static void AttachTaskPaneBridge(SmartWordBridge bridge)
        {
            _confirmationChannel?.AttachBridge(bridge);
            _questionChannel?.AttachBridge(bridge);
            _todoRecoveryChannel?.AttachBridge(bridge);
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
            return NormalizeSettings(settings, null);
        }

        private static SmartWordSettings NormalizeSettings(
            SmartWordSettings settings,
            SmartWordSettings existingSettings)
        {
            var normalized = CloneSettings(settings ?? new SmartWordSettings());
            NormalizeSecret(
                normalized.ApiKey,
                normalized.ProtectedApiKey,
                existingSettings == null ? string.Empty : existingSettings.ApiKey,
                existingSettings == null ? string.Empty : existingSettings.ProtectedApiKey,
                out var apiKey,
                out var protectedApiKey);
            NormalizeSecret(
                normalized.ApiKeyHeavy,
                normalized.ProtectedApiKeyHeavy,
                existingSettings == null ? string.Empty : existingSettings.ApiKeyHeavy,
                existingSettings == null ? string.Empty : existingSettings.ProtectedApiKeyHeavy,
                out var apiKeyHeavy,
                out var protectedApiKeyHeavy);
            NormalizeSecret(
                normalized.ApiKeyLight,
                normalized.ProtectedApiKeyLight,
                existingSettings == null ? string.Empty : existingSettings.ApiKeyLight,
                existingSettings == null ? string.Empty : existingSettings.ProtectedApiKeyLight,
                out var apiKeyLight,
                out var protectedApiKeyLight);

            normalized.ApiKey = apiKey;
            normalized.ProtectedApiKey = protectedApiKey;
            normalized.ApiKeyHeavy = apiKeyHeavy;
            normalized.ProtectedApiKeyHeavy = protectedApiKeyHeavy;
            normalized.ApiKeyLight = apiKeyLight;
            normalized.ProtectedApiKeyLight = protectedApiKeyLight;
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
            normalized.PermissionMode = NormalizePermissionMode(
                normalized.PermissionMode,
                normalized.RequireConfirmationForScripts);
            normalized.RequireConfirmationForScripts =
                IsLegacyConfirmationRequired(normalized.PermissionMode);
            normalized.ContextWindowTokens = normalized.ContextWindowTokens <= 0
                ? 256 * 1024
                : normalized.ContextWindowTokens;
            normalized.ContextSoftLimitRatio = NormalizeRatio(normalized.ContextSoftLimitRatio, 0.65);
            normalized.ContextHardLimitRatio = NormalizeRatio(normalized.ContextHardLimitRatio, 0.85);
            normalized.ContextEmergencyLimitRatio = NormalizeRatio(normalized.ContextEmergencyLimitRatio, 0.95);
            normalized.ContextTokenSafetyMargin = normalized.ContextTokenSafetyMargin <= 0
                ? 1.2
                : normalized.ContextTokenSafetyMargin;
            ApplySecretDisplayFlags(normalized);
            return normalized;
        }

        private static SmartWordSettings CloneSettings(SmartWordSettings settings)
        {
            return new SmartWordSettings
            {
                ApiBaseUrl = settings.ApiBaseUrl,
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                ProtectedApiKey = settings.ProtectedApiKey,
                BaseUrlHeavy = settings.BaseUrlHeavy,
                ApiKeyHeavy = settings.ApiKeyHeavy,
                ProtectedApiKeyHeavy = settings.ProtectedApiKeyHeavy,
                BaseUrlLight = settings.BaseUrlLight,
                ApiKeyLight = settings.ApiKeyLight,
                ProtectedApiKeyLight = settings.ProtectedApiKeyLight,
                LightModel = settings.LightModel,
                HeavyModel = settings.HeavyModel,
                PermissionMode = settings.PermissionMode,
                RequireConfirmationForScripts = settings.RequireConfirmationForScripts,
                ContextWindowTokens = settings.ContextWindowTokens,
                ContextSoftLimitRatio = settings.ContextSoftLimitRatio,
                ContextHardLimitRatio = settings.ContextHardLimitRatio,
                ContextEmergencyLimitRatio = settings.ContextEmergencyLimitRatio,
                ContextTokenSafetyMargin = settings.ContextTokenSafetyMargin,
                CustomInstructions = settings.CustomInstructions,
                HasApiKey = settings.HasApiKey,
                HasApiKeyHeavy = settings.HasApiKeyHeavy,
                HasApiKeyLight = settings.HasApiKeyLight,
                ApiKeyDisplay = settings.ApiKeyDisplay,
                ApiKeyHeavyDisplay = settings.ApiKeyHeavyDisplay,
                ApiKeyLightDisplay = settings.ApiKeyLightDisplay
            };
        }

        private static void ApplySettings(SmartWordSettings source, SmartWordSettings target)
        {
            target.ApiBaseUrl = source.ApiBaseUrl;
            target.BaseUrl = source.BaseUrl;
            target.ApiKey = source.ApiKey;
            target.ProtectedApiKey = source.ProtectedApiKey;
            target.BaseUrlHeavy = source.BaseUrlHeavy;
            target.ApiKeyHeavy = source.ApiKeyHeavy;
            target.ProtectedApiKeyHeavy = source.ProtectedApiKeyHeavy;
            target.BaseUrlLight = source.BaseUrlLight;
            target.ApiKeyLight = source.ApiKeyLight;
            target.ProtectedApiKeyLight = source.ProtectedApiKeyLight;
            target.LightModel = source.LightModel;
            target.HeavyModel = source.HeavyModel;
            target.PermissionMode = source.PermissionMode;
            target.RequireConfirmationForScripts = source.RequireConfirmationForScripts;
            target.ContextWindowTokens = source.ContextWindowTokens;
            target.ContextSoftLimitRatio = source.ContextSoftLimitRatio;
            target.ContextHardLimitRatio = source.ContextHardLimitRatio;
            target.ContextEmergencyLimitRatio = source.ContextEmergencyLimitRatio;
            target.ContextTokenSafetyMargin = source.ContextTokenSafetyMargin;
            target.CustomInstructions = source.CustomInstructions;
            target.HasApiKey = source.HasApiKey;
            target.HasApiKeyHeavy = source.HasApiKeyHeavy;
            target.HasApiKeyLight = source.HasApiKeyLight;
            target.ApiKeyDisplay = source.ApiKeyDisplay;
            target.ApiKeyHeavyDisplay = source.ApiKeyHeavyDisplay;
            target.ApiKeyLightDisplay = source.ApiKeyLightDisplay;
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

        private static SmartWordSettings CreatePersistedSettings(SmartWordSettings normalizedSettings)
        {
            var persisted = CloneSettings(normalizedSettings);
            persisted.ApiKey = string.Empty;
            persisted.ApiKeyHeavy = string.Empty;
            persisted.ApiKeyLight = string.Empty;
            persisted.HasApiKey = false;
            persisted.HasApiKeyHeavy = false;
            persisted.HasApiKeyLight = false;
            persisted.ApiKeyDisplay = string.Empty;
            persisted.ApiKeyHeavyDisplay = string.Empty;
            persisted.ApiKeyLightDisplay = string.Empty;
            return persisted;
        }

        private static SmartWordSettings CreateUiSettingsSnapshot(
            SmartWordSettings runtimeSettings,
            LlmClientOptions llmOptions)
        {
            var snapshot = new SmartWordSettings
            {
                ApiBaseUrl = llmOptions.BaseUrl,
                BaseUrl = llmOptions.BaseUrl,
                ApiKey = string.Empty,
                ProtectedApiKey = string.Empty,
                BaseUrlHeavy = llmOptions.BaseUrlHeavy,
                ApiKeyHeavy = string.Empty,
                ProtectedApiKeyHeavy = string.Empty,
                BaseUrlLight = llmOptions.BaseUrlLight,
                ApiKeyLight = string.Empty,
                ProtectedApiKeyLight = string.Empty,
                LightModel = llmOptions.LightModel,
                HeavyModel = llmOptions.HeavyModel,
                PermissionMode = runtimeSettings.PermissionMode,
                RequireConfirmationForScripts = runtimeSettings.RequireConfirmationForScripts,
                ContextWindowTokens = runtimeSettings.ContextWindowTokens,
                ContextSoftLimitRatio = runtimeSettings.ContextSoftLimitRatio,
                ContextHardLimitRatio = runtimeSettings.ContextHardLimitRatio,
                ContextEmergencyLimitRatio = runtimeSettings.ContextEmergencyLimitRatio,
                ContextTokenSafetyMargin = runtimeSettings.ContextTokenSafetyMargin,
                CustomInstructions = runtimeSettings.CustomInstructions,
                HasApiKey = SecretProtector.HasSecret(runtimeSettings.ApiKey, runtimeSettings.ProtectedApiKey),
                HasApiKeyHeavy = SecretProtector.HasSecret(runtimeSettings.ApiKeyHeavy, runtimeSettings.ProtectedApiKeyHeavy),
                HasApiKeyLight = SecretProtector.HasSecret(runtimeSettings.ApiKeyLight, runtimeSettings.ProtectedApiKeyLight)
            };
            ApplySecretDisplayFlags(snapshot);
            return snapshot;
        }

        private static double NormalizeRatio(double value, double fallback)
        {
            return value > 0 && value < 1
                ? value
                : fallback;
        }

        private static void ApplySecretDisplayFlags(SmartWordSettings settings)
        {
            settings.HasApiKey = SecretProtector.HasSecret(settings.ApiKey, settings.ProtectedApiKey);
            settings.HasApiKeyHeavy = SecretProtector.HasSecret(settings.ApiKeyHeavy, settings.ProtectedApiKeyHeavy);
            settings.HasApiKeyLight = SecretProtector.HasSecret(settings.ApiKeyLight, settings.ProtectedApiKeyLight);
            settings.ApiKeyDisplay = settings.HasApiKey ? MaskedSecretValue : string.Empty;
            settings.ApiKeyHeavyDisplay = settings.HasApiKeyHeavy ? MaskedSecretValue : string.Empty;
            settings.ApiKeyLightDisplay = settings.HasApiKeyLight ? MaskedSecretValue : string.Empty;
        }

        private static void NormalizeSecret(
            string incomingPlainText,
            string incomingProtectedText,
            string existingPlainText,
            string existingProtectedText,
            out string normalizedPlainText,
            out string normalizedProtectedText)
        {
            if (!string.IsNullOrWhiteSpace(incomingPlainText)
                && !IsMaskedSecret(incomingPlainText))
            {
                normalizedPlainText = incomingPlainText;
                normalizedProtectedText = SecretProtector.Protect(incomingPlainText);
                return;
            }

            if (!string.IsNullOrWhiteSpace(incomingProtectedText))
            {
                normalizedProtectedText = incomingProtectedText;
                normalizedPlainText = TryUnprotectSecret(incomingProtectedText);
                return;
            }

            if (!string.IsNullOrWhiteSpace(existingPlainText))
            {
                normalizedPlainText = existingPlainText;
                normalizedProtectedText = string.IsNullOrWhiteSpace(existingProtectedText)
                    ? SecretProtector.Protect(existingPlainText)
                    : existingProtectedText;
                return;
            }

            if (!string.IsNullOrWhiteSpace(existingProtectedText))
            {
                normalizedProtectedText = existingProtectedText;
                normalizedPlainText = TryUnprotectSecret(existingProtectedText);
                return;
            }

            normalizedPlainText = string.Empty;
            normalizedProtectedText = string.Empty;
        }

        private static bool IsMaskedSecret(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized == MaskedSecretValue
                || normalized == "******"
                || normalized == "已保存";
        }

        private static string TryUnprotectSecret(string protectedText)
        {
            try
            {
                return SecretProtector.Unprotect(protectedText);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "解密 SmartWord 本地密钥失败，将按空密钥继续。");
                return string.Empty;
            }
        }

        private static string GetSettingsPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SmartWord",
                "settings.json");
        }

        private static string NormalizePermissionMode(
            string permissionMode,
            bool requireConfirmationForScripts)
        {
            var normalized = (permissionMode ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "read_only":
                case "readonly":
                    return "read_only";
                case "confirm_writes":
                case "confirmwrites":
                    return "confirm_writes";
                case "auto_safe_writes":
                case "autosafewrites":
                    return "auto_safe_writes";
                case "full_auto":
                case "fullauto":
                    return "full_auto";
                default:
                    return requireConfirmationForScripts
                        ? "confirm_writes"
                        : "auto_safe_writes";
            }
        }

        private static bool IsLegacyConfirmationRequired(string permissionMode)
        {
            return !string.Equals(permissionMode, "auto_safe_writes", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(permissionMode, "full_auto", StringComparison.OrdinalIgnoreCase);
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
