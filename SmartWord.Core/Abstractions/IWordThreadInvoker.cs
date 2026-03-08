using System;

// 文件说明：
// 定义 Word COM 线程封送抽象，确保对 COM 对象的访问始终在宿主主线程执行。
namespace SmartWord.Core.Abstractions
{
    /// <summary>
    /// Word 线程调用器契约。
    /// </summary>
    public interface IWordThreadInvoker
    {
        /// <summary>
        /// 在 Word 主线程执行指定逻辑（无返回值）。
        /// </summary>
        /// <param name="action">待执行逻辑。</param>
        void Invoke(Action action);

        /// <summary>
        /// 在 Word 主线程执行指定逻辑并返回结果。
        /// </summary>
        /// <typeparam name="T">返回值类型。</typeparam>
        /// <param name="func">待执行逻辑。</param>
        /// <returns>执行结果。</returns>
        T Invoke<T>(Func<T> func);
    }
}
