using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SmartWord.Services.Prompts
{
    public sealed class PromptCatalogProvider
    {
        private readonly PromptCatalog _catalog;
        private readonly Dictionary<string, PromptVersionItem> _versionMap;

        public PromptCatalogProvider(string catalogPath)
        {
            if (string.IsNullOrWhiteSpace(catalogPath))
            {
                throw new ArgumentException("Prompt catalog path is empty.", nameof(catalogPath));
            }

            if (!File.Exists(catalogPath))
            {
                throw new FileNotFoundException("Prompt catalog file not found.", catalogPath);
            }

            _catalog = Deserialize<PromptCatalog>(File.ReadAllText(catalogPath, Encoding.UTF8));
            if (_catalog == null || _catalog.versions == null || _catalog.versions.Length == 0)
            {
                throw new InvalidOperationException("Prompt catalog is empty.");
            }

            _versionMap = new Dictionary<string, PromptVersionItem>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _catalog.versions.Length; i++)
            {
                PromptVersionItem item = _catalog.versions[i];
                if (item == null || string.IsNullOrWhiteSpace(item.version))
                {
                    continue;
                }

                _versionMap[item.version.Trim()] = item;
            }

            if (_versionMap.Count == 0)
            {
                throw new InvalidOperationException("No valid prompt versions found in catalog.");
            }
        }

        public string[] GetAvailableVersions()
        {
            var result = new List<string>(_versionMap.Keys);
            return result.ToArray();
        }

        public PromptPair BuildRewritePrompts(string requestedVersion, string instruction, string selectedText)
        {
            PromptVersionItem item = ResolveVersion(requestedVersion);
            if (item.rewrite == null)
            {
                throw new InvalidOperationException("Rewrite prompt template is missing.");
            }

            var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "instruction", instruction ?? string.Empty },
                { "selected_text", selectedText ?? string.Empty }
            };

            return new PromptPair
            {
                SystemPrompt = Render(item.rewrite.system, tokens),
                UserPrompt = Render(item.rewrite.userTemplate, tokens)
            };
        }

        public PromptPair BuildVbaPrompts(string requestedVersion, string instruction, string entryPoint)
        {
            PromptVersionItem item = ResolveVersion(requestedVersion);
            if (item.vba == null)
            {
                throw new InvalidOperationException("VBA prompt template is missing.");
            }

            var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "instruction", instruction ?? string.Empty },
                { "entry_point", entryPoint ?? "SmartWord_Run" }
            };

            return new PromptPair
            {
                SystemPrompt = Render(item.vba.system, tokens),
                UserPrompt = Render(item.vba.userTemplate, tokens)
            };
        }

        private PromptVersionItem ResolveVersion(string requestedVersion)
        {
            if (!string.IsNullOrWhiteSpace(requestedVersion))
            {
                PromptVersionItem explicitItem;
                if (_versionMap.TryGetValue(requestedVersion.Trim(), out explicitItem))
                {
                    return explicitItem;
                }
            }

            if (!string.IsNullOrWhiteSpace(_catalog.activeVersion))
            {
                PromptVersionItem activeItem;
                if (_versionMap.TryGetValue(_catalog.activeVersion.Trim(), out activeItem))
                {
                    return activeItem;
                }
            }

            foreach (var pair in _versionMap)
            {
                return pair.Value;
            }

            throw new InvalidOperationException("No prompt version is available.");
        }

        private static string Render(string template, IDictionary<string, string> tokens)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            string output = template;
            foreach (var pair in tokens)
            {
                output = output.Replace("{{" + pair.Key + "}}", pair.Value ?? string.Empty);
            }

            return output;
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

        public sealed class PromptPair
        {
            public string SystemPrompt { get; set; }

            public string UserPrompt { get; set; }
        }

        [DataContract]
        private sealed class PromptCatalog
        {
            [DataMember(Name = "activeVersion")]
            public string activeVersion { get; set; }

            [DataMember(Name = "versions")]
            public PromptVersionItem[] versions { get; set; }
        }

        [DataContract]
        private sealed class PromptVersionItem
        {
            [DataMember(Name = "version")]
            public string version { get; set; }

            [DataMember(Name = "rewrite")]
            public PromptTemplate rewrite { get; set; }

            [DataMember(Name = "vba")]
            public PromptTemplate vba { get; set; }
        }

        [DataContract]
        private sealed class PromptTemplate
        {
            [DataMember(Name = "system")]
            public string system { get; set; }

            [DataMember(Name = "userTemplate")]
            public string userTemplate { get; set; }
        }
    }
}
