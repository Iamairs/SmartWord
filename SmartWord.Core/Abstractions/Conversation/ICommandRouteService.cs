using SmartWord.Core.Models.Conversation;
using System.Threading.Tasks;

namespace SmartWord.Core.Abstractions.Conversation
{
    public interface ICommandRouteService
    {
        Task<RouteDecision> DecideRouteAsync(RouteInput input);
    }
}
