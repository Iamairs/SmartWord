// 文件说明：
// 定义统一通知能力抽象，用于向用户反馈信息与错误。
namespace SmartWord.Core.Abstractions
{
    /// <summary>
    /// 通知服务契约。
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// 发送信息级通知。
        /// </summary>
        /// <param name="message">通知内容。</param>
        void Info(string message);

        /// <summary>
        /// 发送错误级通知。
        /// </summary>
        /// <param name="message">错误内容。</param>
        void Error(string message);
    }
}
