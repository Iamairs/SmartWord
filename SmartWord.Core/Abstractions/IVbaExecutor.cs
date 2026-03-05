// 文件说明：
// 定义 VBA 执行能力抽象，供编排层触发脚本执行。
namespace SmartWord.Core.Abstractions
{
    /// <summary>
    /// VBA 执行器契约。
    /// </summary>
    public interface IVbaExecutor
    {
        /// <summary>
        /// 执行指定 VBA 代码并调用入口过程。
        /// </summary>
        /// <param name="vbaCode">完整 VBA 代码。</param>
        /// <param name="entryPoint">入口过程名称。</param>
        void Execute(string vbaCode, string entryPoint);
    }
}
