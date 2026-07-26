using Xunit;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    /// <summary>
    /// 真实 Word COM 测试必须串行运行，避免活动文档和宿主进程互相干扰。
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class RealWordCollection
    {
        public const string Name = "RealWord";
    }
}
