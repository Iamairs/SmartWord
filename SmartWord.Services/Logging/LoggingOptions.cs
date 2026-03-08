using System;
using System.IO;

namespace SmartWord.Services.Logging
{
    // 文件说明：
    // 日志配置模型，负责统一承载日志级别、落盘路径和滚动策略等运行参数。
    /// <summary>
    /// 日志配置选项。
    /// </summary>
    public sealed class LoggingOptions
    {
        /// <summary>
        /// 日志级别（Verbose/Debug/Information/Warning/Error/Fatal）。
        /// </summary>
        public string LogLevel { get; private set; }

        /// <summary>
        /// 日志目录。
        /// </summary>
        public string LogDirectory { get; private set; }

        /// <summary>
        /// 最多保留的日志文件数量。
        /// </summary>
        public int RetainedFileCountLimit { get; private set; }

        /// <summary>
        /// 单个日志文件大小上限（字节）。
        /// </summary>
        public long FileSizeLimitBytes { get; private set; }

        /// <summary>
        /// 是否启用 Debug 输出（调试器窗口）。
        /// </summary>
        public bool EnableDebugSink { get; private set; }

        /// <summary>
        /// 日志输出模板。
        /// </summary>
        public string OutputTemplate { get; private set; }

        /// <summary>
        /// 创建默认日志配置。
        /// </summary>
        /// <param name="rootDirectory">项目根目录（用于回退路径）。</param>
        /// <returns>默认配置。</returns>
        public static LoggingOptions CreateDefault(string rootDirectory)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string fallbackDirectory = string.IsNullOrWhiteSpace(localAppData)
                ? Path.Combine(rootDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "Config", "logs")
                : Path.Combine(localAppData, "SmartWord", "Logs");

            return new LoggingOptions
            {
                LogLevel = "Information",
                LogDirectory = fallbackDirectory,
                RetainedFileCountLimit = 14,
                FileSizeLimitBytes = 10L * 1024L * 1024L,
                EnableDebugSink = false,
                OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{EventName}] {Message:lj} {Properties:j}{NewLine}{Exception}"
            };
        }

        /// <summary>
        /// 创建并标准化日志配置。
        /// </summary>
        /// <param name="rootDirectory">项目根目录。</param>
        /// <param name="logLevel">日志级别。</param>
        /// <param name="logDirectory">日志目录。</param>
        /// <param name="retainedFileCountLimit">保留文件数。</param>
        /// <param name="fileSizeLimitBytes">文件大小上限。</param>
        /// <param name="enableDebugSink">是否启用 Debug Sink。</param>
        /// <param name="outputTemplate">输出模板。</param>
        /// <returns>标准化后的配置。</returns>
        public static LoggingOptions Create(
            string rootDirectory,
            string logLevel,
            string logDirectory,
            int retainedFileCountLimit,
            long fileSizeLimitBytes,
            bool enableDebugSink,
            string outputTemplate)
        {
            LoggingOptions defaults = CreateDefault(rootDirectory);
            string normalizedDirectory = string.IsNullOrWhiteSpace(logDirectory)
                ? defaults.LogDirectory
                : NormalizePath(rootDirectory, logDirectory);

            int retained = retainedFileCountLimit <= 0 ? defaults.RetainedFileCountLimit : retainedFileCountLimit;
            long fileSize = fileSizeLimitBytes <= 0 ? defaults.FileSizeLimitBytes : fileSizeLimitBytes;
            string template = string.IsNullOrWhiteSpace(outputTemplate) ? defaults.OutputTemplate : outputTemplate.Trim();

            return new LoggingOptions
            {
                LogLevel = NormalizeLogLevel(logLevel, defaults.LogLevel),
                LogDirectory = normalizedDirectory,
                RetainedFileCountLimit = retained,
                FileSizeLimitBytes = fileSize,
                EnableDebugSink = enableDebugSink,
                OutputTemplate = template
            };
        }

        /// <summary>
        /// 标准化日志级别。
        /// </summary>
        public static string NormalizeLogLevel(string level, string fallback)
        {
            if (string.IsNullOrWhiteSpace(level))
            {
                return string.IsNullOrWhiteSpace(fallback) ? "Information" : fallback;
            }

            string value = level.Trim();
            if (string.Equals(value, "Verbose", StringComparison.OrdinalIgnoreCase))
            {
                return "Verbose";
            }

            if (string.Equals(value, "Debug", StringComparison.OrdinalIgnoreCase))
            {
                return "Debug";
            }

            if (string.Equals(value, "Information", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Info", StringComparison.OrdinalIgnoreCase))
            {
                return "Information";
            }

            if (string.Equals(value, "Warning", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Warn", StringComparison.OrdinalIgnoreCase))
            {
                return "Warning";
            }

            if (string.Equals(value, "Error", StringComparison.OrdinalIgnoreCase))
            {
                return "Error";
            }

            if (string.Equals(value, "Fatal", StringComparison.OrdinalIgnoreCase))
            {
                return "Fatal";
            }

            return string.IsNullOrWhiteSpace(fallback) ? "Information" : fallback;
        }

        /// <summary>
        /// 解析并规范化路径。
        /// </summary>
        private static string NormalizePath(string baseDirectory, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmed = path.Trim();
            if (Path.IsPathRooted(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            string root = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;

            return Path.GetFullPath(Path.Combine(root, trimmed));
        }
    }
}
