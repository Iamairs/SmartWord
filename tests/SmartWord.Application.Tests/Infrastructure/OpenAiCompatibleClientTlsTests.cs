using System;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using SmartWord.Infrastructure.LlmClients;
using Xunit;

namespace SmartWord.Application.Tests.Infrastructure
{
    /// <summary>
    /// 验证 LLM 客户端在 Word 宿主中使用稳定的局部 TLS 配置。
    /// </summary>
    public sealed class OpenAiCompatibleClientTlsTests
    {
        [Fact]
        public void CreateHttpClientHandler_UsesTls12WithoutChangingProcessGlobalProtocol()
        {
            var originalProtocol = ServicePointManager.SecurityProtocol;
            try
            {
                var method = typeof(OpenAiCompatibleClient).GetMethod(
                    "CreateHttpClientHandler",
                    BindingFlags.Static | BindingFlags.NonPublic);

                Assert.NotNull(method);

                var handler = method.Invoke(null, null);
                Assert.NotNull(handler);
                try
                {
                    var property = handler.GetType().GetProperty("SslProtocols");
                    Assert.NotNull(property);
                    var protocols = Assert.IsType<SslProtocols>(property.GetValue(handler));
                    Assert.Equal(SslProtocols.Tls12, protocols);
                }
                finally
                {
                    (handler as IDisposable)?.Dispose();
                }

                Assert.Equal(originalProtocol, ServicePointManager.SecurityProtocol);
            }
            finally
            {
                ServicePointManager.SecurityProtocol = originalProtocol;
            }
        }
    }
}
