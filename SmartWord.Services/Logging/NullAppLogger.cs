using SmartWord.Core.Abstractions;
using System;

namespace SmartWord.Services.Logging
{
    // 文件说明：
    // 空日志实现，用于日志初始化失败或未配置时兜底，保证业务流程不受日志系统影响。
    /// <summary>
    /// 空日志器。
    /// </summary>
    public sealed class NullAppLogger : IAppLogger
    {
        private static readonly IDisposable NoopScope = new NoopDisposable();

        /// <summary>
        /// 全局单例。
        /// </summary>
        public static readonly NullAppLogger Instance = new NullAppLogger();

        private NullAppLogger()
        {
        }

        public IDisposable BeginScope(string propertyName, object propertyValue)
        {
            return NoopScope;
        }

        public void Debug(string eventName, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Info(string eventName, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Warn(string eventName, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Error(string eventName, Exception exception, string messageTemplate, params object[] propertyValues)
        {
        }

        public void Fatal(string eventName, Exception exception, string messageTemplate, params object[] propertyValues)
        {
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
