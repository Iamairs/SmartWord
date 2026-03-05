using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SmartWord.Services.Model
{
    public sealed class OpenAiApiOptions
    {
        public string BaseUrl { get; private set; }

        public string ApiKey { get; private set; }

        public string Model { get; private set; }

        public string[] AvailableModels { get; private set; }

        public string PromptCatalogPath { get; private set; }

        public string DefaultPromptVersion { get; private set; }

        public string EmbeddingModel { get; private set; }

        public string ChatStorePath { get; private set; }

        public string VectorIndexDirectory { get; private set; }

        public bool IsConfigured
        {
            get { return !string.IsNullOrWhiteSpace(ApiKey); }
        }

        public string ResolveModel(string overrideModel)
        {
            if (!string.IsNullOrWhiteSpace(overrideModel))
            {
                return overrideModel.Trim();
            }

            return string.IsNullOrWhiteSpace(Model) ? "gpt-4o-mini" : Model.Trim();
        }

        public static OpenAiApiOptions LoadFromEnvironment(string baseDirectory)
        {
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

            string chatStorePath = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_CHAT_STORE_FILE"),
                settingsFile == null ? null : settingsFile.chatStorePath,
                Path.Combine(root, "Config", "chat.sessions.local.json"));

            string vectorIndexDirectory = GetFirstNonEmpty(
                Environment.GetEnvironmentVariable("SMARTWORD_VECTOR_INDEX_DIR"),
                settingsFile == null ? null : settingsFile.vectorIndexDirectory,
                Path.Combine(root, "Config", "vector-index"));

            string[] availableModels = settingsFile == null ? null : settingsFile.availableModels;
            if (availableModels == null || availableModels.Length == 0)
            {
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
                ChatStorePath = NormalizePath(root, chatStorePath),
                VectorIndexDirectory = NormalizePath(root, vectorIndexDirectory)
            };
        }

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

        private static bool ContainsConfigDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return false;
            }

            return Directory.Exists(Path.Combine(directoryPath, "Config"));
        }

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
            [DataMember(Name = "apiBaseUrl")]
            public string apiBaseUrl { get; set; }

            [DataMember(Name = "apiKey")]
            public string apiKey { get; set; }

            [DataMember(Name = "defaultModel")]
            public string defaultModel { get; set; }

            [DataMember(Name = "availableModels")]
            public string[] availableModels { get; set; }

            [DataMember(Name = "promptCatalogPath")]
            public string promptCatalogPath { get; set; }

            [DataMember(Name = "defaultPromptVersion")]
            public string defaultPromptVersion { get; set; }

            [DataMember(Name = "embeddingModel")]
            public string embeddingModel { get; set; }

            [DataMember(Name = "chatStorePath")]
            public string chatStorePath { get; set; }

            [DataMember(Name = "vectorIndexDirectory")]
            public string vectorIndexDirectory { get; set; }
        }
    }
}
