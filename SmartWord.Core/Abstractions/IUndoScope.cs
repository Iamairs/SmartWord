using System;

// 文件说明：
// 定义撤销作用域对象的最小契约，用于将多步操作合并为一个可撤销单元。
namespace SmartWord.Core.Abstractions
{
    /// <summary>
    /// 撤销作用域。
    /// </summary>
    public interface IUndoScope : IDisposable
    {
    }
}
