using System.Windows.Forms;
using SmartWord.Core.Abstractions;

namespace SmartWord.AddIn.Infrastructure
{
    internal sealed class MessageBoxNotificationService : INotificationService
    {
        public void Info(string message)
        {
            MessageBox.Show(message, "SmartWord", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public void Error(string message)
        {
            MessageBox.Show(message, "SmartWord", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
