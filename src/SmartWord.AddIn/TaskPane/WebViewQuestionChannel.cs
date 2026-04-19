using System;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;

namespace SmartWord.AddIn.TaskPane
{
    /// <summary>
    /// Plan 模式采访问答通道，复用 SmartWordBridge 的 TCS 模式。
    /// </summary>
    public sealed class WebViewQuestionChannel : IQuestionChannel
    {
        private readonly object _syncRoot = new object();
        private SmartWordBridge _bridge;

        public bool IsAvailable
        {
            get { lock (_syncRoot) { return _bridge != null; } }
        }

        public void AttachBridge(SmartWordBridge bridge)
        {
            lock (_syncRoot) { _bridge = bridge; }
        }

        public void DetachBridge()
        {
            lock (_syncRoot) { _bridge = null; }
        }

        public Task<string> WaitForAnswerAsync(string questionId, CancellationToken cancellationToken)
        {
            SmartWordBridge bridge;
            lock (_syncRoot) { bridge = _bridge; }

            if (bridge == null)
                throw new InvalidOperationException("当前未挂接可用的问答通道。");

            return bridge.WaitForQuestionAnswerAsync(questionId, cancellationToken);
        }
    }
}
