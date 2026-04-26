using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartWord.OfficeIntegration.Tools
{
    /// <summary>
    /// Office 工具返回给模型和前端的 JSON 应保持中文可读，避免默认转义成 \uXXXX。
    /// </summary>
    internal static class ToolJsonOptions
    {
        public static readonly JsonSerializerOptions Default = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}
