using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmartWord.AddIn.Infrastructure
{
    internal sealed class GlobalHotKeyManager : NativeWindow, IDisposable
    {
        private const int WmHotKey = 0x0312;
        private const uint ModAlt = 0x0001;
        private const uint ModNoRepeat = 0x4000;
        private const uint VkK = 0x4B;
        private const int HotKeyId = 0x534D;

        private readonly Action _onTriggered;
        private bool _isRegistered;
        private bool _isDisposed;

        public GlobalHotKeyManager(Action onTriggered)
        {
            _onTriggered = onTriggered ?? throw new ArgumentNullException(nameof(onTriggered));
            CreateHandle(new CreateParams());
        }

        public void RegisterAltK()
        {
            ThrowIfDisposed();
            if (_isRegistered)
            {
                return;
            }

            _isRegistered = RegisterHotKey(Handle, HotKeyId, ModAlt | ModNoRepeat, VkK);
            if (!_isRegistered)
            {
                throw new InvalidOperationException("Failed to register Alt+K hotkey. It may already be in use.");
            }
        }

        public void Unregister()
        {
            if (!_isRegistered)
            {
                return;
            }

            UnregisterHotKey(Handle, HotKeyId);
            _isRegistered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotKey && m.WParam.ToInt32() == HotKeyId)
            {
                _onTriggered();
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Unregister();
            DestroyHandle();
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(GlobalHotKeyManager));
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
