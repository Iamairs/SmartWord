using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace SmartWord.OfficeIntegration.Scripting
{
    /// <summary>
    /// 基于 Roslyn 执行受控脚本。
    /// </summary>
    public class CSharpScriptExecutor
    {
        public async Task<ScriptExecutionResult> ExecuteAsync(
            string code,
            ScriptGlobals scriptGlobals,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scriptBody = string.Join(
                Environment.NewLine,
                "dynamic app = App ?? WordApp;",
                "dynamic doc = Doc ?? ActiveDoc;",
                "dynamic wordApp = WordApp ?? App;",
                "dynamic activeDoc = ActiveDoc ?? Doc;",
                code ?? string.Empty);

            var options = ScriptOptions.Default
                .AddReferences(
                    typeof(object).Assembly,
                    typeof(Enumerable).Assembly,
                    typeof(ScriptGlobals).Assembly)
                .AddImports(
                    "System",
                    "System.Linq",
                    "System.Collections.Generic");

            var state = await CSharpScript.RunAsync(
                    scriptBody,
                    options,
                    scriptGlobals,
                    typeof(ScriptGlobals),
                    cancellationToken)
                .ConfigureAwait(false);

            var logOutput = scriptGlobals == null ? string.Empty : scriptGlobals.GetOutput();
            var output = string.IsNullOrWhiteSpace(logOutput)
                ? (state.ReturnValue == null ? "脚本执行完成。" : Convert.ToString(state.ReturnValue))
                : logOutput;

            return new ScriptExecutionResult
            {
                Success = true,
                Output = output ?? string.Empty,
                LogOutput = logOutput ?? string.Empty,
                ReturnValue = state.ReturnValue,
                ReturnValueType = state.ReturnValue == null ? string.Empty : state.ReturnValue.GetType().FullName ?? string.Empty
            };
        }
    }

    public class ScriptExecutionResult
    {
        public bool Success { get; set; }

        public string Output { get; set; } = string.Empty;

        public string LogOutput { get; set; } = string.Empty;

        public object ReturnValue { get; set; }

        public string ReturnValueType { get; set; } = string.Empty;
    }
}
