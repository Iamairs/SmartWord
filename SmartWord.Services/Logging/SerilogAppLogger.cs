using Serilog;
using Serilog.Context;
using SmartWord.Core.Abstractions;
using System;

namespace SmartWord.Services.Logging
{
    // 文件说明：
    // Serilog 适配器，将项目统一日志抽象映射到 Serilog 结构化日志实现。
    /// <summary>
    /// Serilog 日志器适配实现。
    /// </summary>
    public sealed class SerilogAppLogger : IAppLogger
    {
        private readonly ILogger _logger;

        /// <summary>
        /// 初始化 Serilog 适配日志器。
        /// </summary>
        /// <param name="logger">Serilog logger。</param>
        public SerilogAppLogger(ILogger logger)
        {
            _logger = logger ?? Log.Logger;
        }

        /// <summary>
        /// 开始一个新的日志作用域，注入指定属性到上下文中。返回的 IDisposable 对象在作用域结束时释放，自动清理上下文属性。
        /// </summary>
        /// <param name="propertyName">属性名称，不能为空或全空格</param>
        /// <param name="propertyValue">属性值</param>
        /// <returns></returns>
        public IDisposable BeginScope(string propertyName, object propertyValue)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return EmptyScope.Instance;
            }

            return LogContext.PushProperty(propertyName.Trim(), propertyValue);
        }

        public void Debug(string eventName, string messageTemplate, params object[] propertyValues)
        {
            ForEvent(eventName).Debug(messageTemplate ?? string.Empty, propertyValues ?? new object[0]);
        }

        public void Info(string eventName, string messageTemplate, params object[] propertyValues)
        {
            ForEvent(eventName).Information(messageTemplate ?? string.Empty, propertyValues ?? new object[0]);
        }

        public void Warn(string eventName, string messageTemplate, params object[] propertyValues)
        {
            ForEvent(eventName).Warning(messageTemplate ?? string.Empty, propertyValues ?? new object[0]);
        }

        public void Error(string eventName, Exception exception, string messageTemplate, params object[] propertyValues)
        {
            ForEvent(eventName).Error(exception, messageTemplate ?? string.Empty, propertyValues ?? new object[0]);
        }

        public void Fatal(string eventName, Exception exception, string messageTemplate, params object[] propertyValues)
        {
            ForEvent(eventName).Fatal(exception, messageTemplate ?? string.Empty, propertyValues ?? new object[0]);
        }

        /// <summary>
        /// 注入统一 EventName 字段，便于日志平台检索。
        /// </summary>
        private ILogger ForEvent(string eventName)
        {
            string normalized = string.IsNullOrWhiteSpace(eventName) ? "app.log" : eventName.Trim();
            return _logger.ForContext("EventName", normalized);
        }

        private sealed class EmptyScope : IDisposable
        {
            public static readonly EmptyScope Instance = new EmptyScope();

            public void Dispose()
            {
            }
        }
    }
}
