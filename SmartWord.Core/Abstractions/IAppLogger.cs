using System;

namespace SmartWord.Core.Abstractions
{
    // 文件说明：
    // 统一应用日志抽象，屏蔽具体日志库实现，便于在不同层级复用结构化日志能力。
    /// <summary>
    /// 应用日志接口。
    /// </summary>
    public interface IAppLogger
    {
        /// <summary>
        /// 将结构化属性压入当前日志上下文。
        /// </summary>
        /// <param name="propertyName">属性名。</param>
        /// <param name="propertyValue">属性值。</param>
        /// <returns>释放后移除该上下文属性。</returns>
        IDisposable BeginScope(string propertyName, object propertyValue);

        /// <summary>
        /// 记录调试日志。
        /// </summary>
        void Debug(string eventName, string messageTemplate, params object[] propertyValues);

        /// <summary>
        /// 记录信息日志。
        /// </summary>
        void Info(string eventName, string messageTemplate, params object[] propertyValues);

        /// <summary>
        /// 记录告警日志。
        /// </summary>
        void Warn(string eventName, string messageTemplate, params object[] propertyValues);

        /// <summary>
        /// 记录错误日志。
        /// </summary>
        void Error(string eventName, Exception exception, string messageTemplate, params object[] propertyValues);

        /// <summary>
        /// 记录致命日志。
        /// </summary>
        void Fatal(string eventName, Exception exception, string messageTemplate, params object[] propertyValues);
    }
}
