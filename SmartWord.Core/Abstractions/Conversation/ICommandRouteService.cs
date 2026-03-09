using SmartWord.Core.Models.Conversation;
using System.Threading;
using System.Threading.Tasks;

// 文件说明：
// 定义会话路由决策服务抽象，用于在改写、VBA 与混合链路间进行分流。
namespace SmartWord.Core.Abstractions.Conversation
{
    /// <summary>
    /// 指令路由服务契约。
    /// </summary>
    public interface ICommandRouteService
    {
        /// <summary>
        /// 根据输入上下文做路由判定。
        /// </summary>
        /// <param name="input">路由输入信息。</param>
        /// <returns>路由决策结果。</returns>
        Task<RouteDecision> DecideRouteAsync(RouteInput input, CancellationToken cancellationToken = default(CancellationToken));
    }
}
