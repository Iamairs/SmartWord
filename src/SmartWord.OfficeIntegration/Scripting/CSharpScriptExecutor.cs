using System;
using System.Threading;
using System.Threading.Tasks;

namespace SmartWord.OfficeIntegration.Scripting
{
    /// <summary>
    /// 预留 Roslyn 脚本执行器骨架。
    /// </summary>
    public class CSharpScriptExecutor
    {
        public Task<ScriptExecutionResult> ExecuteAsync(
            string code,
            ScriptGlobals scriptGlobals,
            CancellationToken cancellationToken)
        {
            _ = code;
            _ = scriptGlobals;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotImplementedException("Roslyn 脚本执行将在后续阶段接入。");
        }
    }

    public class ScriptExecutionResult
    {
        public bool Success { get; set; }

        public string Output { get; set; } = string.Empty;
    }
}
