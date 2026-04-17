using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Enums;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 定义单个工具的输入、权限和执行协议。
    /// </summary>
    public interface ITool
    {
        string Name { get; }

        string Description { get; }

        ToolPermission RequiredPermission { get; }

        bool IsVisibleToModel { get; }

        JsonElement InputSchema { get; }

        Task<ToolCallResult> ExecuteAsync(
            JsonElement input,
            IUndoScope undoScope,
            CancellationToken cancellationToken);
    }
}
