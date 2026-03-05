using System.Windows.Forms;
using SmartWord.Core.Abstractions;

namespace SmartWord.AddIn.Infrastructure
{
    // 文件说明：
    // 提供基于 MessageBox 的通知实现，适用于 VSTO 宿主中的即时用户提示。
    /// <summary>
    /// 通知服务实现：使用消息框展示信息与错误。
    /// </summary>
    internal sealed class MessageBoxNotificationService : INotificationService
    {
        /// <summary>
        /// 以信息级别弹窗提示用户。
        /// </summary>
        /// <param name="message">提示内容。</param>
        public void Info(string message)
        {
            MessageBox.Show(message, "SmartWord", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 以错误级别弹窗提示用户。
        /// </summary>
        /// <param name="message">错误内容。</param>
        public void Error(string message)
        {
            MessageBox.Show(message, "SmartWord", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
