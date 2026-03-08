using Serilog;
using Serilog.Events;
using SmartWord.Core.Abstractions;
using System;
using System.IO;
using System.Text;

namespace SmartWord.Services.Logging
{
    // 文件说明：
    // 日志启动器，负责创建并管理全局 Serilog 实例，供 AddIn 生命周期初始化与释放调用。
    /// <summary>
    /// 日志启动器。
    /// </summary>
    public static class LoggingBootstrapper
    {
        /// <summary>
        /// 初始化日志系统并返回统一日志抽象。
        /// </summary>
        /// <param name="options">日志配置。</param>
        /// <returns>日志器实例；初始化失败时返回空日志器。</returns>
        public static IAppLogger Initialize(LoggingOptions options)
        {
            LoggingOptions effective = options ?? LoggingOptions.CreateDefault(AppDomain.CurrentDomain.BaseDirectory);
            try
            {
                if (!Directory.Exists(effective.LogDirectory))
                {
                    Directory.CreateDirectory(effective.LogDirectory);
                }

                string filePath = Path.Combine(effective.LogDirectory, "smartword-.log");

                var configuration = new LoggerConfiguration()
                    .MinimumLevel.Is(ParseLevel(effective.LogLevel))
                    .Enrich.FromLogContext()
                    .WriteTo.File(
                        filePath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: effective.RetainedFileCountLimit,
                        fileSizeLimitBytes: effective.FileSizeLimitBytes,
                        rollOnFileSizeLimit: true,
                        shared: true,
                        encoding: Encoding.UTF8,
                        outputTemplate: effective.OutputTemplate);

                if (effective.EnableDebugSink)
                {
                    configuration = configuration.WriteTo.Debug(outputTemplate: effective.OutputTemplate);
                }

                Log.Logger = configuration.CreateLogger();
                var logger = new SerilogAppLogger(Log.Logger);
                logger.Info(
                    "app.logger.initialized",
                    "Logger initialized. Level={LogLevel} Directory={LogDirectory} RetainedFiles={RetainedFiles} FileSizeLimit={FileSizeLimitBytes}",
                    effective.LogLevel,
                    effective.LogDirectory,
                    effective.RetainedFileCountLimit,
                    effective.FileSizeLimitBytes);
                return logger;
            }
            catch
            {
                return NullAppLogger.Instance;
            }
        }

        /// <summary>
        /// 关闭日志系统并强制刷新缓冲。
        /// </summary>
        public static void Shutdown()
        {
            Log.CloseAndFlush();
        }

        /// <summary>
        /// 将文本级别解析为 Serilog 级别。
        /// </summary>
        private static LogEventLevel ParseLevel(string level)
        {
            string normalized = LoggingOptions.NormalizeLogLevel(level, "Information");
            if (string.Equals(normalized, "Verbose", StringComparison.OrdinalIgnoreCase))
            {
                return LogEventLevel.Verbose;
            }

            if (string.Equals(normalized, "Debug", StringComparison.OrdinalIgnoreCase))
            {
                return LogEventLevel.Debug;
            }

            if (string.Equals(normalized, "Warning", StringComparison.OrdinalIgnoreCase))
            {
                return LogEventLevel.Warning;
            }

            if (string.Equals(normalized, "Error", StringComparison.OrdinalIgnoreCase))
            {
                return LogEventLevel.Error;
            }

            if (string.Equals(normalized, "Fatal", StringComparison.OrdinalIgnoreCase))
            {
                return LogEventLevel.Fatal;
            }

            return LogEventLevel.Information;
        }
    }
}
