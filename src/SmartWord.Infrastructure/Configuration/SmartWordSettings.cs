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

        public string ApiKey { get; set; } = string.Empty;

        public string BaseUrlHeavy { get; set; } = string.Empty;

        public string ApiKeyHeavy { get; set; } = string.Empty;

        public string BaseUrlLight { get; set; } = string.Empty;

        public string ApiKeyLight { get; set; } = string.Empty;

        public string LightModel { get; set; } = "gpt-4.1-mini";

        public string HeavyModel { get; set; } = "gpt-4.1";

        public string PermissionMode { get; set; } = string.Empty;

        public bool RequireConfirmationForScripts { get; set; } = true;

        public string CustomInstructions { get; set; } = string.Empty;
    }
}
