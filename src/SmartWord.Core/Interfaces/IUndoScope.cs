using System;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 抽象任务级撤销范围，便于编排层统一提交或回滚。
    /// </summary>
    public interface IUndoScope : IDisposable
    {
        void Commit();

        void Rollback();
    }
}
