using System.Threading;
using System.Threading.Tasks;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 抽象任务级撤销范围工厂，便于编排层在启动任务时创建 UndoScope。
    /// </summary>
    public interface IUndoScopeFactory
    {
        Task<IUndoScope> BeginTaskUndoAsync(string operationName, CancellationToken cancellationToken);
    }
}
