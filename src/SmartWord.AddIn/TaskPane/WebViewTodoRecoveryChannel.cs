using System;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;

namespace SmartWord.AddIn.TaskPane
{
    /// <summary>
    /// Todo Board 恢复决策通道，负责等待前端返回恢复方式。
    /// </summary>
    public sealed class WebViewTodoRecoveryChannel : ITodoRecoveryChannel
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

        public Task<TodoBoardRecoveryDecision> WaitForDecisionAsync(
            string recoveryRequestId,
            CancellationToken cancellationToken)
        {
            SmartWordBridge bridge;
            lock (_syncRoot)
            {
                bridge = _bridge;
            }

            if (bridge == null)
            {
                throw new InvalidOperationException("当前未挂接可用的 Todo 恢复决策通道。");
            }

            return bridge.WaitForTodoBoardRecoveryDecisionAsync(recoveryRequestId, cancellationToken);
        }
    }
}
