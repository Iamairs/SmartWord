using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartWord.Application.Context;
using SmartWord.Application.Orchestration;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Todo;
using SmartWord.Application.Tools;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Core.Telemetry;
using SmartWord.Infrastructure.LlmClients;
using SmartWord.Infrastructure.Persistence;
using SmartWord.Infrastructure.Telemetry;
using SmartWord.OfficeIntegration.Scripting;
using SmartWord.OfficeIntegration.Tools;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.EvalRunner
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return MainAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static async Task<int> MainAsync(string[] args)
        {
            var options = EvalRunnerOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(EvalRunnerOptions.HelpText);
                return 0;
            }

            Directory.CreateDirectory(options.Output);
            var runId = new DirectoryInfo(options.Output).Name;
            var tracePath = Path.Combine(options.Output, "trace.jsonl");
            var sqlitePath = Path.Combine(options.Output, "eval.sqlite");
            var telemetrySink = new CompositeTelemetrySink(
                new JsonlAgentTelemetrySink(tracePath),
                new SqliteEvalTelemetrySink(sqlitePath));

            await telemetrySink.RecordAsync(CreateRunEvent("eval_run_started", runId, options), CancellationToken.None)
                .ConfigureAwait(false);

            var cases = BenchmarkCaseLoader.Load(options.CasesRoot, options)
                .Take(options.MaxCases <= 0 ? int.MaxValue : options.MaxCases)
                .ToList();
            var results = new List<CaseRunResult>();
            foreach (var benchmarkCase in cases)
            {
                Console.WriteLine("Running " + benchmarkCase.Id);
                var result = await RunCaseAsync(runId, options, benchmarkCase, telemetrySink).ConfigureAwait(false);
                results.Add(result);
            }

            ReportWriter.Write(options.Output, runId, options, results);
            await telemetrySink.RecordAsync(CreateRunEvent("eval_run_completed", runId, options), CancellationToken.None)
                .ConfigureAwait(false);

            Console.WriteLine("Benchmark run completed: " + options.Output);
            return results.Any(r => string.Equals(r.Status, "failed", StringComparison.OrdinalIgnoreCase)) ? 2 : 0;
        }

        private static async Task<CaseRunResult> RunCaseAsync(
            string runId,
            EvalRunnerOptions options,
            BenchmarkCase benchmarkCase,
            IAgentTelemetrySink telemetrySink)
        {
            var taskRunId = runId + "-" + benchmarkCase.Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var caseOutputDir = Path.Combine(options.Output, "cases", benchmarkCase.Id);
            Directory.CreateDirectory(caseOutputDir);
            var inputCopyPath = Path.Combine(caseOutputDir, "input.docx");
            var outputDocxPath = Path.Combine(caseOutputDir, "output.docx");
            File.Copy(benchmarkCase.InputDocxPath, inputCopyPath, true);
            File.Copy(benchmarkCase.TaskJsonPath, Path.Combine(caseOutputDir, "task.json"), true);
            File.Copy(benchmarkCase.ExpectedJsonPath, Path.Combine(caseOutputDir, "expected.json"), true);

            var context = new AgentTelemetryContext
            {
                EvalRunId = runId,
                TaskRunId = taskRunId,
                CaseId = benchmarkCase.Id,
                Level = "L" + benchmarkCase.Level,
                Variant = options.Variant
            };

            using (new AgentTelemetryScope(context))
            {
                object wordApp = null;
                object openedDocument = null;
                WordApplicationWrapper wordWrapper = null;
                try
                {
                    await telemetrySink.RecordAsync(
                            CreateTaskStartedEvent(runId, taskRunId, benchmarkCase, options, inputCopyPath, outputDocxPath),
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    wordApp = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application"));
                    ProgramAccessor.SetComProperty(wordApp, "Visible", options.KeepWordVisible);
                    dynamic documents = ProgramAccessor.GetComProperty(wordApp, "Documents");
                    openedDocument = documents.Open(inputCopyPath, ReadOnly: false, Visible: options.KeepWordVisible);
                    if (openedDocument == null)
                    {
                        throw new InvalidOperationException("Word 未返回已打开的文档对象。");
                    }

                    ProgramAccessor.InvokeComMethod(openedDocument, "Activate");

                    wordWrapper = new WordApplicationWrapper(wordApp);
                    var activeDocumentPath = await wordWrapper.GetActiveDocumentPath().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(activeDocumentPath))
                    {
                        throw new InvalidOperationException("Word 已启动，但没有活动文档。请检查 input.docx 是否被受保护视图或 Word 启动弹窗阻塞。");
                    }

                    var orchestrator = BuildOrchestrator(wordWrapper, options, telemetrySink);
                    var runOptions = CreateAgentRunOptions(benchmarkCase, options);

                    await foreach (var ignored in orchestrator.RunAsync(
                        benchmarkCase.UserInstruction,
                        runOptions,
                        CancellationToken.None).ConfigureAwait(false))
                    {
                        // EvalRunner 只消费事件以推动运行；正式指标来自 telemetry 和 scorer。
                    }

                    dynamic activeDocument = openedDocument;
                    activeDocument.SaveAs2(outputDocxPath);
                    activeDocument.Close(false);
                    openedDocument = null;
                    ProgramAccessor.QuitWord(wordApp);

                    var score = BenchmarkScorer.Score(
                        benchmarkCase,
                        inputCopyPath,
                        outputDocxPath,
                        Path.Combine(options.Output, "trace.jsonl"));
                    var scorePath = Path.Combine(caseOutputDir, "score.json");
                    File.WriteAllText(scorePath, JsonConvert.SerializeObject(score, Formatting.Indented));

                    await telemetrySink.RecordAsync(
                            CreateScoreEvent(runId, taskRunId, benchmarkCase, options, score),
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    await telemetrySink.RecordAsync(
                            CreateTaskCompletedEvent(runId, taskRunId, benchmarkCase, options, inputCopyPath, outputDocxPath, score),
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    return new CaseRunResult
                    {
                        CaseId = benchmarkCase.Id,
                        Level = benchmarkCase.Level,
                        Variant = options.Variant,
                        Status = "completed",
                        Score = score.Score,
                        Pass = score.Pass,
                        StrictPass = score.StrictPass,
                        SafetyViolation = score.SafetyViolation,
                        OutputDocx = outputDocxPath,
                        ScorePath = scorePath,
                        CheckResults = score.Checks
                    };
                }
                catch (Exception ex)
                {
                    var rootException = UnwrapException(ex);
                    ProgramAccessor.TryCloseWord(wordApp);
                    var failed = new CaseRunResult
                    {
                        CaseId = benchmarkCase.Id,
                        Level = benchmarkCase.Level,
                        Variant = options.Variant,
                        Status = "failed",
                        FailureReason = rootException.Message,
                        FailureDetail = rootException.ToString(),
                        OutputDocx = File.Exists(outputDocxPath) ? outputDocxPath : string.Empty
                    };
                    await telemetrySink.RecordAsync(
                            CreateTaskFailedEvent(runId, taskRunId, benchmarkCase, options, inputCopyPath, outputDocxPath, rootException),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    File.WriteAllText(
                        Path.Combine(caseOutputDir, "score.json"),
                        JsonConvert.SerializeObject(failed, Formatting.Indented));
                    return failed;
                }
                finally
                {
                    wordWrapper?.Dispose();
                    ProgramAccessor.TryCloseDocument(openedDocument);
                }
            }
        }

        private static Exception UnwrapException(Exception exception)
        {
            var current = exception;
            while (current is System.Reflection.TargetInvocationException targetInvocationException
                && targetInvocationException.InnerException != null)
            {
                current = targetInvocationException.InnerException;
            }

            return current ?? exception;
        }

        private static IAgentOrchestrator BuildOrchestrator(
            WordApplicationWrapper wordWrapper,
            EvalRunnerOptions options,
            IAgentTelemetrySink telemetrySink)
        {
            var llmOptions = new LlmClientOptions
            {
                BaseUrl = options.BaseUrl,
                ApiKey = options.ApiKey,
                HeavyModel = options.Model,
                LightModel = options.Model,
                TimeoutSeconds = options.TimeoutSeconds
            };
            ILlmClient llmClient = string.IsNullOrWhiteSpace(options.ApiKey)
                ? new TelemetryLlmClient(new FailingLlmClient("未配置真实 LLM API Key。请通过 --api-key 或 SMARTWORD_EVAL_API_KEY/OPENAI_API_KEY 提供。"), telemetrySink)
                : new TelemetryLlmClient(new OpenAiCompatibleClient(llmOptions), telemetrySink);
            var conversationStore = new InMemoryConversationStore();
            var promptDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Prompts");
            if (!Directory.Exists(promptDirectory))
            {
                promptDirectory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "SmartWord.AddIn", "Resources", "Prompts"));
            }

            var registry = BuildToolRegistry(wordWrapper);
            var permissionGuard = new PermissionGuard(registry);
            var confirmationChannel = new BenchmarkConfirmationChannel(options.AutoConfirmPolicy);
            var todoStore = new JsonTodoStore(Path.Combine(options.Output, "todo"));
            var todoManager = new TodoManager(todoStore);
            return new AgentOrchestrator(
                llmClient,
                new ContextHydrator(wordWrapper),
                conversationStore,
                new SystemPromptBuilder(promptDirectory),
                registry,
                permissionGuard,
                confirmationChannel,
                wordWrapper,
                new ConversationCompressor(),
                null,
                null,
                todoManager,
                new TodoReminderService(),
                null,
                null,
                null,
                null,
                telemetrySink);
        }

        private static ToolRegistry BuildToolRegistry(WordApplicationWrapper wordWrapper)
        {
            var scriptSecurityValidator = new ScriptSecurityValidator();
            var scriptExecutor = new CSharpScriptExecutor();
            var registry = new ToolRegistry();
            registry.Register(new ProbeDocumentTool(wordWrapper));
            registry.Register(new ReadSectionTool(wordWrapper));
            registry.Register(new GrepDocumentTool(wordWrapper));
            registry.Register(new GetSelectionContextTool(wordWrapper));
            registry.Register(new ReadTableTool(wordWrapper));
            registry.Register(new ReadAnnotationsTool(wordWrapper));
            registry.Register(new ReadScriptTool(wordWrapper, scriptExecutor, scriptSecurityValidator));
            registry.Register(new VerifyScriptTool(wordWrapper, scriptExecutor, scriptSecurityValidator));
            registry.Register(new PatchRangeTool(wordWrapper));
            registry.Register(new ExecuteScriptTool(wordWrapper, scriptExecutor, scriptSecurityValidator));
            registry.Register(new AskUserQuestionTool());
            return registry;
        }

        private static AgentRunOptions CreateAgentRunOptions(BenchmarkCase benchmarkCase, EvalRunnerOptions options)
        {
            return new AgentRunOptions
            {
                Mode = ParseMode(benchmarkCase.Mode),
                Model = options.Model,
                PermissionMode = ParsePermission(options.Permission),
                RequireConfirmationForScripts = benchmarkCase.RequiresConfirmation,
                MaxIterations = options.MaxIterations,
                EnableToolCalling = true
            };
        }

        private static AgentMode ParseMode(string mode)
        {
            return Enum.TryParse(mode ?? string.Empty, true, out AgentMode parsed)
                ? parsed
                : AgentMode.Agent;
        }

        private static AgentPermissionMode ParsePermission(string permission)
        {
            return Enum.TryParse(permission ?? string.Empty, true, out AgentPermissionMode parsed)
                ? parsed
                : AgentPermissionMode.ConfirmWrites;
        }

        private static AgentTelemetryEvent CreateRunEvent(string eventType, string runId, EvalRunnerOptions options)
        {
            var e = AgentTelemetryEvent.Create(eventType);
            e.EvalRunId = runId;
            e.Variant = options.Variant;
            e.Model = options.Model;
            e.Data["outputDir"] = options.Output;
            e.Data["status"] = eventType.EndsWith("completed", StringComparison.OrdinalIgnoreCase) ? "completed" : "running";
            return e;
        }

        private static AgentTelemetryEvent CreateTaskStartedEvent(
            string runId,
            string taskRunId,
            BenchmarkCase benchmarkCase,
            EvalRunnerOptions options,
            string inputDocx,
            string outputDocx)
        {
            var e = CreateCaseEvent("task_started", runId, taskRunId, benchmarkCase, options);
            e.Data["inputDocx"] = inputDocx;
            e.Data["outputDocx"] = outputDocx;
            e.Data["startedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");
            e.Data["status"] = "running";
            return e;
        }

        private static AgentTelemetryEvent CreateTaskCompletedEvent(
            string runId,
            string taskRunId,
            BenchmarkCase benchmarkCase,
            EvalRunnerOptions options,
            string inputDocx,
            string outputDocx,
            ScoreResult score)
        {
            var e = CreateCaseEvent("task_completed", runId, taskRunId, benchmarkCase, options);
            e.Data["inputDocx"] = inputDocx;
            e.Data["outputDocx"] = outputDocx;
            e.Data["status"] = "completed";
            e.Data["score"] = score.Score;
            return e;
        }

        private static AgentTelemetryEvent CreateTaskFailedEvent(
            string runId,
            string taskRunId,
            BenchmarkCase benchmarkCase,
            EvalRunnerOptions options,
            string inputDocx,
            string outputDocx,
            Exception ex)
        {
            var e = CreateCaseEvent("task_failed", runId, taskRunId, benchmarkCase, options);
            e.Data["inputDocx"] = inputDocx;
            e.Data["outputDocx"] = outputDocx;
            e.Data["status"] = "failed";
            e.Data["failureType"] = ex is TimeoutException ? "timeout" : "unknown_error";
            e.Data["failureReason"] = ex.Message;
            e.Data["failureDetail"] = ex.ToString();
            return e;
        }

        private static AgentTelemetryEvent CreateScoreEvent(
            string runId,
            string taskRunId,
            BenchmarkCase benchmarkCase,
            EvalRunnerOptions options,
            ScoreResult score)
        {
            var e = CreateCaseEvent("score_completed", runId, taskRunId, benchmarkCase, options);
            e.Data["score"] = score.Score;
            e.Data["pass"] = score.Pass;
            e.Data["strictPass"] = score.StrictPass;
            e.Data["safetyViolation"] = score.SafetyViolation;
            e.Data["checks"] = score.Checks;
            return e;
        }

        private static AgentTelemetryEvent CreateCaseEvent(
            string eventType,
            string runId,
            string taskRunId,
            BenchmarkCase benchmarkCase,
            EvalRunnerOptions options)
        {
            var e = AgentTelemetryEvent.Create(eventType);
            e.EvalRunId = runId;
            e.TaskRunId = taskRunId;
            e.CaseId = benchmarkCase.Id;
            e.Level = "L" + benchmarkCase.Level;
            e.Variant = options.Variant;
            e.Mode = benchmarkCase.Mode;
            e.PermissionMode = options.Permission;
            e.Model = options.Model;
            return e;
        }

    }
}
