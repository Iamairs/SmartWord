using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Core.Models;

namespace SmartWord.Core.Interfaces
{
    /// <summary>
    /// 抽象任务历史审计的写入与查询能力。
    /// </summary>
    public interface ITaskHistoryStore
    {
        Task<TaskRunRecord> StartRunAsync(
            TaskRunStartRequest request,
            CancellationToken cancellationToken);

        Task RecordToolAsync(
            string taskRunId,
            TaskToolRecord record,
            CancellationToken cancellationToken);

        Task RecordChangeAsync(
            string taskRunId,
            TaskChangeRecord record,
            CancellationToken cancellationToken);

        Task CompleteRunAsync(
            string taskRunId,
            TaskRunCompletion completion,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<TaskRunRecord>> GetRecentRunsAsync(
            string documentPath,
            int limit,
            CancellationToken cancellationToken);

        Task<TaskRunDetail> GetRunDetailAsync(
            string taskRunId,
            CancellationToken cancellationToken);
    }
}
