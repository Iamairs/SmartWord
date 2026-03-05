using System.Threading.Tasks;

// 文件说明：
// 定义文本向量化能力抽象，供检索与语义匹配能力复用。
namespace SmartWord.Core.Abstractions.Conversation
{
    /// <summary>
    /// 向量化服务契约。
    /// </summary>
    public interface IEmbeddingService
    {
        /// <summary>
        /// 为输入文本生成向量表示。
        /// </summary>
        /// <param name="input">待向量化文本。</param>
        /// <param name="modelOverride">模型覆盖项；为空时由实现层选择默认模型。</param>
        /// <returns>向量数组。</returns>
        Task<float[]> CreateEmbeddingAsync(string input, string modelOverride);
    }
}
