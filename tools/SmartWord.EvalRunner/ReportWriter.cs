using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SmartWord.EvalRunner
{
    internal static class ReportWriter
    {
        public static void Write(string outputDir, string runId, EvalRunnerOptions options, IReadOnlyList<CaseRunResult> results)
        {
            File.WriteAllText(
                Path.Combine(outputDir, "summary.json"),
                JsonConvert.SerializeObject(BuildSummary(runId, options, results), Formatting.Indented));
            WriteTaskResults(Path.Combine(outputDir, "task_results.csv"), results);
            WriteToolResults(Path.Combine(outputDir, "tool_results.csv"));
            WriteMarkdown(Path.Combine(outputDir, "eval_report.md"), runId, options, results);
        }

        private static object BuildSummary(string runId, EvalRunnerOptions options, IReadOnlyList<CaseRunResult> results)
        {
            var total = Math.Max(1, results.Count);
            return new
            {
                run_id = runId,
                variant = options.Variant,
                model = options.Model,
                total_tasks = results.Count,
                pass_rate = results.Count(r => r.Pass) / (double)total,
                strict_pass_rate = results.Count(r => r.StrictPass) / (double)total,
                safety_violation_rate = results.Count(r => r.SafetyViolation) / (double)total,
                avg_score = results.Count == 0 ? 0 : results.Average(r => r.Score),
                by_level = results
                    .GroupBy(r => "L" + r.Level)
                    .ToDictionary(
                        g => g.Key,
                        g => new
                        {
                            total = g.Count(),
                            pass_rate = g.Count(r => r.Pass) / (double)Math.Max(1, g.Count()),
                            strict_pass_rate = g.Count(r => r.StrictPass) / (double)Math.Max(1, g.Count())
                        })
            };
        }

        private static void WriteTaskResults(string path, IReadOnlyList<CaseRunResult> results)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("case_id,level,variant,status,score,pass,strict_pass,safety_violation,total_tokens,llm_calls,tool_calls,failed_tools,accurate_tools,duration_ms,output_docx");
                foreach (var result in results)
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        Csv(result.CaseId),
                        "L" + result.Level,
                        Csv(result.Variant),
                        Csv(result.Status),
                        result.Score.ToString(CultureInfo.InvariantCulture),
                        result.Pass ? "true" : "false",
                        result.StrictPass ? "true" : "false",
                        result.SafetyViolation ? "true" : "false",
                        "",
                        "",
                        "",
                        "",
                        "",
                        "",
                        Csv(result.OutputDocx)
                    }));
                }
            }
        }

        private static void WriteToolResults(string path)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("case_id,tool_call_id,tool_name,success,is_relevant,is_accurate,failure_type,duration_ms,requires_confirmation,was_confirmed");
            }
        }

        private static void WriteMarkdown(string path, string runId, EvalRunnerOptions options, IReadOnlyList<CaseRunResult> results)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("# SmartWord Benchmark Report");
                writer.WriteLine();
                writer.WriteLine("- Run: " + runId);
                writer.WriteLine("- Variant: " + options.Variant);
                writer.WriteLine("- Model: " + options.Model);
                writer.WriteLine("- Temperature: " + options.Temperature.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine("- Cases: " + results.Count);
                writer.WriteLine();
                writer.WriteLine("## Results");
                writer.WriteLine();
                writer.WriteLine("| Case | Level | Score | Pass | Strict | Safety | Status |");
                writer.WriteLine("| --- | --- | ---: | --- | --- | --- | --- |");
                foreach (var result in results)
                {
                    writer.WriteLine($"| {result.CaseId} | L{result.Level} | {result.Score.ToString(CultureInfo.InvariantCulture)} | {result.Pass} | {result.StrictPass} | {result.SafetyViolation} | {result.Status} |");
                }

                var failed = results.Where(r => !r.Pass || string.Equals(r.Status, "failed", StringComparison.OrdinalIgnoreCase)).ToList();
                writer.WriteLine();
                writer.WriteLine("## Failed Cases");
                writer.WriteLine();
                if (failed.Count == 0)
                {
                    writer.WriteLine("No failed cases.");
                }
                else
                {
                    foreach (var result in failed)
                    {
                        writer.WriteLine("- " + result.CaseId + ": " + (string.IsNullOrWhiteSpace(result.FailureReason) ? "score below threshold or safety violation" : result.FailureReason));
                    }
                }
            }
        }

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }

    internal sealed class CaseRunResult
    {
        public string CaseId { get; set; } = string.Empty;
        public int Level { get; set; }
        public string Variant { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public double Score { get; set; }
        public bool Pass { get; set; }
        public bool StrictPass { get; set; }
        public bool SafetyViolation { get; set; }
        public string OutputDocx { get; set; } = string.Empty;
        public string ScorePath { get; set; } = string.Empty;
        public string FailureReason { get; set; } = string.Empty;
        public List<CheckResult> CheckResults { get; set; } = new List<CheckResult>();
    }
}
