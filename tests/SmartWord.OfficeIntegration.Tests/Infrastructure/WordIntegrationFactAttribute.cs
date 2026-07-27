using System;
using Xunit;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    /// <summary>
    /// 仅在显式开启真实 Word 集成测试时执行，避免普通测试意外启动 Word。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class WordIntegrationFactAttribute : FactAttribute
    {
        public WordIntegrationFactAttribute()
        {
            if (!string.Equals(
                Environment.GetEnvironmentVariable("SMARTWORD_RUN_WORD_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
            {
                Skip = "设置 SMARTWORD_RUN_WORD_INTEGRATION=1 后运行真实 Word 集成测试。";
            }
        }
    }
}
