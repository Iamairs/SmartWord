using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmartWord.AddIn.Infrastructure
{
    // 文件说明：
    // 提供全局热键（Alt+K）注册与消息分发能力，供 AddIn 在任意焦点状态下快速唤起对话侧栏。
    /// <summary>
    /// 全局热键管理器。
    /// 基于隐藏窗口接收 <c>WM_HOTKEY</c> 消息，并将命中事件回调给上层。
    /// </summary>
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

        /// <summary>
        /// 初始化热键管理器并创建消息窗口句柄。
        /// </summary>
        /// <param name="onTriggered">热键触发后的回调。</param>
        public GlobalHotKeyManager(Action onTriggered)
        {
            _onTriggered = onTriggered ?? throw new ArgumentNullException(nameof(onTriggered));
            CreateHandle(new CreateParams());
        }

        /// <summary>
        /// 注册 Alt+K 全局热键。
        /// </summary>
        /// <exception cref="ObjectDisposedException">对象已释放时抛出。</exception>
        /// <exception cref="InvalidOperationException">热键被占用或注册失败时抛出。</exception>
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

            // 无论返回值如何，都将本地状态重置，避免重复反注册导致状态错乱。
            UnregisterHotKey(Handle, HotKeyId);
            _isRegistered = false;
        }

        /// <summary>
        /// 处理窗口消息：命中目标热键时触发业务回调。
        /// </summary>
        /// <param name="m">Windows 消息结构。</param>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotKey && m.WParam.ToInt32() == HotKeyId)
            {
                _onTriggered();
            }

            base.WndProc(ref m);
        }

        /// <summary>
        /// 释放热键与窗口句柄。
        /// </summary>
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

        /// <summary>
        /// 对外部调用做释放态保护，避免访问失效句柄。
        /// </summary>
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
