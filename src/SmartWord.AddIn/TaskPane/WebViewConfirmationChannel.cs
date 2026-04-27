using System;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.AddIn.TaskPane
{
    /// <summary>
    /// 复用当前 TaskPane 中真实存在的 SmartWordBridge，实现写前确认等待。
    /// </summary>
    public sealed class WebViewConfirmationChannel : IToolConfirmationChannel
    {
        private readonly object _syncRoot = new object();
        private SmartWordBridge _bridge;

        public bool IsAvailable
        {
            get
            {
                lock (_syncRoot)
                {
                    return _bridge != null;
                }
            }
        }

        public void AttachBridge(SmartWordBridge bridge)
        {
            lock (_syncRoot)
            {
                _bridge = bridge;
            }
        }

        public void DetachBridge()
        {
            lock (_syncRoot)
            {
                _bridge = null;
            }
        }

        public Task<bool> WaitForConfirmationAsync(string toolCallId, CancellationToken cancellationToken)
        {
            SmartWordBridge bridge;
            lock (_syncRoot)
            {
                bridge = _bridge;
            }

            if (bridge == null)
            {
                throw new InvalidOperationException("当前未挂接可用的 WebView 确认通道。");
            }

            return bridge.WaitForToolConfirmationAsync(toolCallId, cancellationToken);
        }

        public Task<ToolConfirmationDecision> WaitForConfirmationDecisionAsync(
            ToolConfirmationRequest request,
            CancellationToken cancellationToken)
        {
            SmartWordBridge bridge;
            lock (_syncRoot)
            {
                bridge = _bridge;
            }

            if (bridge == null)
            {
                throw new InvalidOperationException("当前未挂接可用的 WebView 确认通道。");
            }

            return bridge.WaitForToolConfirmationDecisionAsync(request, cancellationToken);
        }
    }
}
