using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

// 文件说明：
// Prompt 目录提供器，负责读取版本化模板并渲染为最终系统/用户提示词。
namespace SmartWord.Services.Prompts
{
    /// <summary>
    /// Prompt 目录提供器。
    /// </summary>
    public sealed class PromptCatalogProvider
    {
        private readonly PromptCatalog _catalog;
        private readonly Dictionary<string, PromptVersionItem> _versionMap;

        /// <summary>
        /// 初始化 Prompt 目录提供器。
        /// </summary>
        /// <param name="catalogPath">Prompt 配置文件路径。</param>
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

        /// <summary>
        /// 获取可用 Prompt 版本列表。
        /// </summary>
        /// <returns>版本号数组。</returns>
        public string[] GetAvailableVersions()
        {
            var result = new List<string>(_versionMap.Keys);
            return result.ToArray();
        }

        /// <summary>
        /// 构建写作场景提示词。
        /// </summary>
        /// <param name="requestedVersion">请求版本。</param>
        /// <param name="instruction">用户指令。</param>
        /// <param name="selectedText">选中文本。</param>
        /// <returns>系统/用户提示词对。</returns>
        public PromptPair BuildWritingPrompts(string requestedVersion, string instruction, string selectedText)
        {
            PromptVersionItem item = ResolveVersion(requestedVersion);
            PromptTemplate template = item.writing ?? item.rewrite;
            if (template == null)
            {
                throw new InvalidOperationException("Writing prompt template is missing.");
            }

            var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "instruction", instruction ?? string.Empty },
                { "selected_text", selectedText ?? string.Empty },
                { "retrieved_context", string.Empty }
            };

            return new PromptPair
            {
                SystemPrompt = Render(template.system, tokens),
                UserPrompt = Render(template.userTemplate, tokens)
            };
        }

        /// <summary>
        /// 构建处理场景提示词。
        /// </summary>
        /// <param name="requestedVersion">请求版本。</param>
        /// <param name="instruction">用户指令。</param>
        /// <param name="selectedText">选中文本。</param>
        /// <param name="retrievedContext">检索上下文。</param>
        /// <returns>系统/用户提示词对。</returns>
        public PromptPair BuildProcessingPrompts(string requestedVersion, string instruction, string selectedText, string retrievedContext)
        {
            PromptVersionItem item = ResolveVersion(requestedVersion);
            PromptTemplate template = item.processing ?? item.writing ?? item.rewrite;
            if (template == null)
            {
                throw new InvalidOperationException("Processing prompt template is missing.");
            }

            var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "instruction", instruction ?? string.Empty },
                { "selected_text", selectedText ?? string.Empty },
                { "retrieved_context", retrievedContext ?? string.Empty }
            };

            return new PromptPair
            {
                SystemPrompt = Render(template.system, tokens),
                UserPrompt = Render(template.userTemplate, tokens)
            };
        }

        /// <summary>
        /// 构建问答场景提示词。
        /// </summary>
        /// <param name="requestedVersion">请求版本。</param>
        /// <param name="question">用户问题。</param>
        /// <param name="selectedText">选中文本。</param>
        /// <param name="retrievedContext">检索上下文。</param>
        /// <returns>系统/用户提示词对。</returns>
        public PromptPair BuildQaPrompts(string requestedVersion, string question, string selectedText, string retrievedContext)
        {
            PromptVersionItem item = ResolveVersion(requestedVersion);
            PromptTemplate template = item.qa;
            if (template == null)
            {
                throw new InvalidOperationException("QA prompt template is missing.");
            }

            var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "question", question ?? string.Empty },
                { "selected_text", selectedText ?? string.Empty },
                { "retrieved_context", retrievedContext ?? string.Empty }
            };

            return new PromptPair
            {
                SystemPrompt = Render(template.system, tokens),
                UserPrompt = Render(template.userTemplate, tokens)
            };
        }

        /// <summary>
        /// 构建执行场景提示词。
        /// </summary>
        /// <param name="requestedVersion">请求版本。</param>
        /// <param name="instruction">用户指令。</param>
        /// <param name="selectedText">选中文本。</param>
        /// <param name="entryPoint">入口过程名称。</param>
        /// <param name="retrievedContext">检索上下文。</param>
        /// <returns>系统/用户提示词对。</returns>
        public PromptPair BuildExecutePrompts(string requestedVersion, string instruction, string selectedText, string entryPoint, string retrievedContext)
        {
            PromptVersionItem item = ResolveVersion(requestedVersion);
            PromptTemplate template = item.execute ?? item.vba;
            if (template == null)
            {
                throw new InvalidOperationException("Execute prompt template is missing.");
            }

            var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "instruction", instruction ?? string.Empty },
                { "selected_text", selectedText ?? string.Empty },
                { "entry_point", entryPoint ?? "SmartWord_Run" },
                { "retrieved_context", retrievedContext ?? string.Empty }
            };

            return new PromptPair
            {
                SystemPrompt = Render(template.system, tokens),
                UserPrompt = Render(template.userTemplate, tokens)
            };
        }

        /// <summary>
        /// 构建改写场景提示词（兼容旧接口）。
        /// </summary>
        /// <param name="requestedVersion">请求版本。</param>
        /// <param name="instruction">用户指令。</param>
        /// <param name="selectedText">选中文本。</param>
        /// <returns>系统/用户提示词对。</returns>
        public PromptPair BuildRewritePrompts(string requestedVersion, string instruction, string selectedText)
        {
            return BuildWritingPrompts(requestedVersion, instruction, selectedText);
        }

        /// <summary>
        /// 构建 VBA 场景提示词（兼容旧接口）。
        /// </summary>
        /// <param name="requestedVersion">请求版本。</param>
        /// <param name="instruction">用户指令。</param>
        /// <param name="selectedText">选中文本。</param>
        /// <param name="entryPoint">入口过程名称。</param>
        /// <returns>系统/用户提示词对。</returns>
        public PromptPair BuildVbaPrompts(string requestedVersion, string instruction, string selectedText, string entryPoint)
        {
            return BuildExecutePrompts(requestedVersion, instruction, selectedText, entryPoint, string.Empty);
        }

        /// <summary>
        /// 解析最终使用的版本（请求版本 -> activeVersion -> 第一个可用版本）。
        /// </summary>
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

        /// <summary>
        /// 执行模板渲染，将占位符替换为实际值。
        /// </summary>
        private static string Render(string template, IDictionary<string, string> tokens)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            string output = template;
            foreach (var pair in tokens)
            {
                // 采用简单字符串替换，避免引入额外模板引擎依赖。
                output = output.Replace("{{" + pair.Key + "}}", pair.Value ?? string.Empty);
            }

            return output;
        }

        /// <summary>
        /// 反序列化 JSON 为目标对象。
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

        /// <summary>
        /// 提示词对。
        /// </summary>
        public sealed class PromptPair
        {
            /// <summary>
            /// 系统提示词。
            /// </summary>
            public string SystemPrompt { get; set; }

            /// <summary>
            /// 用户提示词。
            /// </summary>
            public string UserPrompt { get; set; }
        }

        [DataContract]
        private sealed class PromptCatalog
        {
            /// <summary>
            /// 当前激活版本。
            /// </summary>
            [DataMember(Name = "activeVersion")]
            public string activeVersion { get; set; }

            /// <summary>
            /// 全部版本集合。
            /// </summary>
            [DataMember(Name = "versions")]
            public PromptVersionItem[] versions { get; set; }
        }

        [DataContract]
        private sealed class PromptVersionItem
        {
            /// <summary>
            /// 版本号。
            /// </summary>
            [DataMember(Name = "version")]
            public string version { get; set; }

            /// <summary>
            /// 旧版改写模板。
            /// </summary>
            [DataMember(Name = "rewrite")]
            public PromptTemplate rewrite { get; set; }

            /// <summary>
            /// 旧版 VBA 模板。
            /// </summary>
            [DataMember(Name = "vba")]
            public PromptTemplate vba { get; set; }

            /// <summary>
            /// 写作模板。
            /// </summary>
            [DataMember(Name = "writing")]
            public PromptTemplate writing { get; set; }

            /// <summary>
            /// 处理模板。
            /// </summary>
            [DataMember(Name = "processing")]
            public PromptTemplate processing { get; set; }

            /// <summary>
            /// 问答模板。
            /// </summary>
            [DataMember(Name = "qa")]
            public PromptTemplate qa { get; set; }

            /// <summary>
            /// 执行模板。
            /// </summary>
            [DataMember(Name = "execute")]
            public PromptTemplate execute { get; set; }
        }

        [DataContract]
        private sealed class PromptTemplate
        {
            /// <summary>
            /// 系统提示词模板。
            /// </summary>
            [DataMember(Name = "system")]
            public string system { get; set; }

            /// <summary>
            /// 用户提示词模板。
            /// </summary>
            [DataMember(Name = "userTemplate")]
            public string userTemplate { get; set; }
        }
    }
}
