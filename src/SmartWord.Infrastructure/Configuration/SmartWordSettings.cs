namespace SmartWord.Infrastructure.Configuration
{
    /// <summary>
    /// 表示用户级应用设置，并持久化到 settings.json。
    /// </summary>
    public class SmartWordSettings
    {
        /// <summary>
        /// 兼容旧字段，读取时可作为 BaseUrl 的回退来源。
        /// </summary>
        public string ApiBaseUrl { get; set; } = string.Empty;

        public string BaseUrl { get; set; } = "https://api.openai.com/v1";

        /// <summary>
        /// 运行期明文 API Key。保存到磁盘前必须清空，持久化使用 ProtectedApiKey。
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        public string ProtectedApiKey { get; set; } = string.Empty;

        public string BaseUrlHeavy { get; set; } = string.Empty;

        /// <summary>
        /// 运行期明文重量模型 API Key。保存到磁盘前必须清空。
        /// </summary>
        public string ApiKeyHeavy { get; set; } = string.Empty;

        public string ProtectedApiKeyHeavy { get; set; } = string.Empty;

        public string BaseUrlLight { get; set; } = string.Empty;

        /// <summary>
        /// 运行期明文轻量模型 API Key。保存到磁盘前必须清空。
        /// </summary>
        public string ApiKeyLight { get; set; } = string.Empty;

        public string ProtectedApiKeyLight { get; set; } = string.Empty;

        public string LightModel { get; set; } = "gpt-4.1-mini";

        public string HeavyModel { get; set; } = "gpt-4.1";

        public string PermissionMode { get; set; } = string.Empty;

        public bool RequireConfirmationForScripts { get; set; } = true;

        public string CustomInstructions { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonIgnore]
        public bool HasApiKey { get; set; }

        [Newtonsoft.Json.JsonIgnore]
        public bool HasApiKeyHeavy { get; set; }

        [Newtonsoft.Json.JsonIgnore]
        public bool HasApiKeyLight { get; set; }

        [Newtonsoft.Json.JsonIgnore]
        public string ApiKeyDisplay { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonIgnore]
        public string ApiKeyHeavyDisplay { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonIgnore]
        public string ApiKeyLightDisplay { get; set; } = string.Empty;
    }
}
