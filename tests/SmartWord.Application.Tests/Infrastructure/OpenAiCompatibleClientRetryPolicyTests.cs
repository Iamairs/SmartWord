using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SmartWord.Infrastructure.LlmClients;
using Xunit;

namespace SmartWord.Application.Tests.Infrastructure
{
    /// <summary>
    /// 验证 LLM 客户端对瞬时网络错误的重试判定。
    /// </summary>
    public sealed class OpenAiCompatibleClientRetryPolicyTests
    {
        [Fact]
        public void IsTransientSendException_WebExceptionWrappingSocketFailure_ReturnsTrue()
        {
            var exception = new WebException(
                "基础连接已经关闭。",
                new IOException(
                    "无法从传输连接中读取数据。",
                    new SocketException((int)SocketError.TimedOut)),
                WebExceptionStatus.ConnectionClosed,
                null);

            var result = InvokeIsTransientSendException(exception);

            Assert.True(result);
        }

        [Fact]
        public void IsTransientSendException_OperationCanceled_ReturnsFalse()
        {
            var result = InvokeIsTransientSendException(new OperationCanceledException("请求已取消。"));

            Assert.False(result);
        }

        private static bool InvokeIsTransientSendException(Exception exception)
        {
            var method = typeof(OpenAiCompatibleClient).GetMethod(
                "IsTransientSendException",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var result = method.Invoke(null, new object[] { exception });
            return Assert.IsType<bool>(result);
        }
    }
}
