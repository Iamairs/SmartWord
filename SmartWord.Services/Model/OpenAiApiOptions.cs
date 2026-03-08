using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using SmartWord.Services.Logging;

// 文件说明：
// OpenAI 兼容服务配置模型，负责聚合环境变量与本地配置文件并输出标准化运行参数。
namespace SmartWord.Services.Model
{
    /// <summary>
    /// OpenAI API 配置选项。
    /// </summary>
    public sealed class OpenAiApiOptions
    {
        /// <summary>
        /// API 基础地址。
        /// </summary>
        public string BaseUrl { get; private set; }

        /// <summary>
        /// API 密钥。
        /// </summary>
        public string ApiKey { get; private set; }

        public string EmbeddingBaseUrl { get; private set; }

        public string EmbeddingApiKey { get; private set; }

        /// <summary>
        /// 默认聊天模型。
        /// </summary>
        public string Model { get; private set; }

        /// <summary>
        /// 可选模型列表。
        /// </summary>
        public string[] AvailableModels { get; private set; }

        /// <summary>
        /// Prompt 目录文件路径。
        /// </summary>
        public string PromptCatalogPath { get; private set; }

        /// <summary>
        /// 默认 Prompt 版本。
        /// </summary>
        public string DefaultPromptVersion { get; private set; }

        /// <summary>
        /// 向量模型名称。
        /// </summary>
        public string EmbeddingModel { get; private set; }

        /// <summary>
        /// 会话存储文件路径。
        /// </summary>
        public string ChatStorePath { get; private set; }

        /// <summary>
        /// 向量索引目录。
        /// </summary>
        public string VectorIndexDirectory { get; private set; }

        /// <summary>
        /// 日志配置。
        /// </summary>
        public LoggingOptions Logging { get; private set; }

        public bool IsConfigured
        {
            get { return !string.IsNullOrWhiteSpace(ApiKey); }
        }

        public bool IsEmbeddingConfigured
        {
            get { return !string.IsNullOrWhiteSpace(ResolveEmbeddingApiKey()); }
        }

        /// <summary>
        /// 解析最终使用模型，优先使用调用方覆盖项。
        /// </summary>
        /// <param name="overrideModel">覆盖模型名。</param>
        /// <returns>最终模型名。</returns>
        public string ResolveModel(string overrideModel)
        {
            if (!string.IsNullOrWhiteSpace(overrideModel))
            {
                return overrideModel.Trim();
            }

            return string.IsNullOrWhiteSpace(Model) ? "gpt-4o-mini" : Model.Trim();
        }

        public string ResolveEmbeddingBaseUrl()
        {
            if (!string.IsNullOrWhiteSpace(EmbeddingBaseUrl))
            {
                return NormalizeBaseUrl(EmbeddingBaseUrl);
            }

            return NormalizeBaseUrl(BaseUrl);
        }

        public string ResolveEmbeddingApiKey()
        {
            if (!string.IsNullOrWhiteSpace(EmbeddingApiKey))
            {
                return EmbeddingApiKey.Trim();
            }

            return ApiKey;
        }

        /// <summary>
        /// 从环境变量与本地配置文件加载运行配置。
        /// </summary>
        /// <param name="baseDirectory">基准目录。</param>
        /// <returns>标准化配置对象。</returns>
        public static OpenAiApiOptions LoadFromEnvironment(string baseDirectory)
        {
            // 先解析项目根目录，确保相对路径配置在不同启动目录下仍可用。
            string root = ResolveRootDirectory(baseDirectory);

            string settingsFilePathRaw = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_SETTINGS_FILE"),
                Path.Combine(root, "Config", "runtime-settings.local.json"));
            string settingsFilePath = NormalizePath(root, settingsFilePathRaw);

            RuntimeSettingsFile settingsFile = LoadSettingsFile(settingsFilePath);

            string baseUrl = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_API_BASE_URL"),
                Environment.GetEnvironmentVariable("OPENAI_BASE_URL"),
                settingsFile == null ? null : settingsFile.apiBaseUrl,
                "https://api.openai.com/v1");

            string apiKey = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_API_KEY"),
                Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
                settingsFile == null ? null : settingsFile.apiKey);

            string model = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_API_MODEL"),
                Environment.GetEnvironmentVariable("OPENAI_MODEL"),
                settingsFile == null ? null : settingsFile.defaultModel,
                "gpt-4o-mini");

            string promptCatalogPath = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_PROMPTS_FILE"),
                settingsFile == null ? null : settingsFile.promptCatalogPath,
                Path.Combine(root, "Config", "prompts.catalog.json"));

            string defaultPromptVersion = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_PROMPT_VERSION"),
                settingsFile == null ? null : settingsFile.defaultPromptVersion);

            string embeddingModel = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_EMBEDDING_MODEL"),
                settingsFile == null ? null : settingsFile.embeddingModel,
                "text-embedding-3-small");

            string embeddingBaseUrl = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_EMBEDDING_API_BASE_URL"),
                settingsFile == null ? null : settingsFile.embeddingApiBaseUrl,
                baseUrl);

            string embeddingApiKey = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_EMBEDDING_API_KEY"),
                settingsFile == null ? null : settingsFile.embeddingApiKey,
                apiKey);

            string chatStorePath = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_CHAT_STORE_FILE"),
                settingsFile == null ? null : settingsFile.chatStorePath,
                Path.Combine(root, "Config", "chat.sessions.local.json"));

            string vectorIndexDirectory = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_VECTOR_INDEX_DIR"),
                settingsFile == null ? null : settingsFile.vectorIndexDirectory,
                Path.Combine(root, "Config", "vector-index"));

            LoggingOptions defaultLogging = LoggingOptions.CreateDefault(root);
            string loggingLevel = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_LOG_LEVEL"),
                settingsFile == null || settingsFile.logging == null ? null : settingsFile.logging.logLevel,
                defaultLogging.LogLevel);

            string loggingDirectory = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_LOG_DIR"),
                settingsFile == null || settingsFile.logging == null ? null : settingsFile.logging.logDirectory,
                defaultLogging.LogDirectory);

            int retainedFileCountLimit = GetFirstValidInt(
                Environment.GetEnvironmentVariable("SMARTWORD_LOG_RETAINED_FILES"),
                settingsFile == null || settingsFile.logging == null ? null : settingsFile.logging.retainedFileCountLimit,
                defaultLogging.RetainedFileCountLimit);

            long fileSizeLimitBytes = GetFirstValidLong(
                Environment.GetEnvironmentVariable("SMARTWORD_LOG_FILE_SIZE_BYTES"),
                settingsFile == null || settingsFile.logging == null ? null : settingsFile.logging.fileSizeLimitBytes,
                defaultLogging.FileSizeLimitBytes);

            bool enableDebugSink = GetFirstValidBool(
                Environment.GetEnvironmentVariable("SMARTWORD_LOG_DEBUG_SINK"),
                settingsFile == null || settingsFile.logging == null ? null : settingsFile.logging.enableDebugSink,
                defaultLogging.EnableDebugSink);

            string outputTemplate = GetFirstNonEmpty(
                settingsFile == null || settingsFile.logging == null ? null : settingsFile.logging.outputTemplate,
                defaultLogging.OutputTemplate);

            LoggingOptions loggingOptions = LoggingOptions.Create(
                root,
                loggingLevel,
                loggingDirectory,
                retainedFileCountLimit,
                fileSizeLimitBytes,
                enableDebugSink,
                outputTemplate);

            string[] availableModels = settingsFile == null ? null : settingsFile.availableModels;
            if (availableModels == null || availableModels.Length == 0)
            {
                // 可选模型为空时至少保留一个默认模型，避免 UI 下拉框为空。
                availableModels = new[] { model };
            }

            return new OpenAiApiOptions
            {
                BaseUrl = NormalizeBaseUrl(baseUrl),
                ApiKey = apiKey,
                Model = model,
                AvailableModels = NormalizeModels(availableModels, model),
                PromptCatalogPath = NormalizePath(root, promptCatalogPath),
                DefaultPromptVersion = defaultPromptVersion,
                EmbeddingModel = embeddingModel,
                EmbeddingBaseUrl = NormalizeBaseUrl(embeddingBaseUrl),
                EmbeddingApiKey = embeddingApiKey,
                ChatStorePath = NormalizePath(root, chatStorePath),
                VectorIndexDirectory = NormalizePath(root, vectorIndexDirectory),
                Logging = loggingOptions
            };
        }

        /// <summary>
        /// 解析配置根目录。
        /// </summary>
        /// <param name="baseDirectory">基准目录。</param>
        /// <returns>根目录绝对路径。</returns>
        private static string ResolveRootDirectory(string baseDirectory)
        {
            string executionBase = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;

            if (string.IsNullOrWhiteSpace(executionBase))
            {
                executionBase = Directory.GetCurrentDirectory();
            }

            string normalized = Path.GetFullPath(executionBase.Trim());
            string trimmed = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (IsDebugOrReleaseOutputFolder(trimmed))
            {
                DirectoryInfo outputDir = new DirectoryInfo(trimmed);
                if (outputDir.Parent != null && outputDir.Parent.Parent != null)
                {
                    string projectRootCandidate = outputDir.Parent.Parent.FullName;
                    if (ContainsConfigDirectory(projectRootCandidate))
                    {
                        return projectRootCandidate;
                    }
                }
            }

            if (ContainsConfigDirectory(trimmed))
            {
                return trimmed;
            }

            return trimmed;
        }

        /// <summary>
        /// 判断路径是否位于 Debug/Release 输出目录。
        /// </summary>
        private static bool IsDebugOrReleaseOutputFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalized = path.Replace('/', '\\');
            return normalized.EndsWith("\\bin\\Debug", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith("\\bin\\Release", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断目录中是否包含 <c>Config</c> 子目录。
        /// </summary>
        private static bool ContainsConfigDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return false;
            }

            return Directory.Exists(Path.Combine(directoryPath, "Config"));
        }

        /// <summary>
        /// 读取并反序列化运行时设置文件。
        /// </summary>
        private static RuntimeSettingsFile LoadSettingsFile(string settingsFilePath)
        {
            if (string.IsNullOrWhiteSpace(settingsFilePath))
            {
                return null;
            }

            string normalizedPath = settingsFilePath.Trim();
            if (!File.Exists(normalizedPath))
            {
                return null;
            }

            string json = File.ReadAllText(normalizedPath, Encoding.UTF8);
            return Deserialize<RuntimeSettingsFile>(json);
        }

        /// <summary>
        /// 标准化模型列表（去重、去空并补默认值）。
        /// </summary>
        private static string[] NormalizeModels(string[] models, string fallbackModel)
        {
            if (models == null || models.Length == 0)
            {
                return new[] { fallbackModel };
            }

            var result = new System.Collections.Generic.List<string>();
            for (int i = 0; i < models.Length; i++)
            {
                string current = models[i] == null ? string.Empty : models[i].Trim();
                if (current.Length > 0 && !result.Contains(current))
                {
                    result.Add(current);
                }
            }

            if (result.Count == 0)
            {
                result.Add(fallbackModel);
            }

            return result.ToArray();
        }

        /// <summary>
        /// 返回第一个非空字符串。
        /// </summary>
        private static string GetFirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return values[i].Trim();
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 解析第一个有效整数，失败时回退默认值。
        /// </summary>
        private static int GetFirstValidInt(string first, string second, int fallback)
        {
            int value;
            if (int.TryParse(first, out value) && value > 0)
            {
                return value;
            }

            if (int.TryParse(second, out value) && value > 0)
            {
                return value;
            }

            return fallback;
        }

        /// <summary>
        /// 解析第一个有效长整型，失败时回退默认值。
        /// </summary>
        private static long GetFirstValidLong(string first, string second, long fallback)
        {
            long value;
            if (long.TryParse(first, out value) && value > 0L)
            {
                return value;
            }

            if (long.TryParse(second, out value) && value > 0L)
            {
                return value;
            }

            return fallback;
        }

        /// <summary>
        /// 解析第一个有效布尔值，失败时回退默认值。
        /// </summary>
        private static bool GetFirstValidBool(string first, string second, bool fallback)
        {
            bool value;
            if (bool.TryParse(first, out value))
            {
                return value;
            }

            if (bool.TryParse(second, out value))
            {
                return value;
            }

            return fallback;
        }

        /// <summary>
        /// 标准化基础地址并移除尾部斜杠。
        /// </summary>
        private static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return "https://api.openai.com/v1";
            }

            string trimmed = baseUrl.Trim();
            while (trimmed.EndsWith("/", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed;
        }

        /// <summary>
        /// 解析路径：支持相对路径转绝对路径。
        /// </summary>
        private static string NormalizePath(string baseDirectory, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmed = path.Trim();
            if (Path.IsPathRooted(trimmed))
            {
                return trimmed;
            }

            return Path.GetFullPath(Path.Combine(baseDirectory, trimmed));
        }

        /// <summary>
        /// 将 JSON 字符串反序列化为目标对象。
        /// </summary>
        private static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new MemoryStream(bytes))
            {
                return serializer.ReadObject(stream) as T;
            }
        }

        [DataContract]
        private sealed class RuntimeSettingsFile
        {
            /// <summary>
            /// API 基础地址。
            /// </summary>
            [DataMember(Name = "apiBaseUrl")]
            public string apiBaseUrl { get; set; }

            /// <summary>
            /// API 密钥。
            /// </summary>
            [DataMember(Name = "apiKey")]
            public string apiKey { get; set; }

            /// <summary>
            /// 默认模型。
            /// </summary>
            [DataMember(Name = "defaultModel")]
            public string defaultModel { get; set; }

            /// <summary>
            /// 可选模型列表。
            /// </summary>
            [DataMember(Name = "availableModels")]
            public string[] availableModels { get; set; }

            /// <summary>
            /// Prompt 配置文件路径。
            /// </summary>
            [DataMember(Name = "promptCatalogPath")]
            public string promptCatalogPath { get; set; }

            /// <summary>
            /// 默认 Prompt 版本。
            /// </summary>
            [DataMember(Name = "defaultPromptVersion")]
            public string defaultPromptVersion { get; set; }

            /// <summary>
            /// 向量模型名称。
            /// </summary>
            [DataMember(Name = "embeddingModel")]
            public string embeddingModel { get; set; }

            [DataMember(Name = "embeddingApiBaseUrl")]
            public string embeddingApiBaseUrl { get; set; }

            [DataMember(Name = "embeddingApiKey")]
            public string embeddingApiKey { get; set; }

            /// <summary>
            /// 会话存储文件路径。
            /// </summary>
            [DataMember(Name = "chatStorePath")]
            public string chatStorePath { get; set; }

            /// <summary>
            /// 向量索引目录。
            /// </summary>
            [DataMember(Name = "vectorIndexDirectory")]
            public string vectorIndexDirectory { get; set; }

            [DataMember(Name = "logging")]
            public RuntimeLoggingSettings logging { get; set; }
        }

        [DataContract]
        private sealed class RuntimeLoggingSettings
        {
            [DataMember(Name = "logLevel")]
            public string logLevel { get; set; }

            [DataMember(Name = "logDirectory")]
            public string logDirectory { get; set; }

            [DataMember(Name = "retainedFileCountLimit")]
            public string retainedFileCountLimit { get; set; }

            [DataMember(Name = "fileSizeLimitBytes")]
            public string fileSizeLimitBytes { get; set; }

            [DataMember(Name = "enableDebugSink")]
            public string enableDebugSink { get; set; }

            [DataMember(Name = "outputTemplate")]
            public string outputTemplate { get; set; }
        }
    }
}

