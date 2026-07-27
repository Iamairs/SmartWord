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
            var totalExpectedPoints = results.Sum(r => r.TotalExpectedPoints);
            var scoredPoints = results.Sum(r => r.ScoredPoints);
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
                coverage_rate = totalExpectedPoints <= 0 ? 0 : scoredPoints / totalExpectedPoints,
                unsupported_count = results.Sum(r => r.CheckResults.Count(c => c.Status == CheckStatuses.Unsupported)),
                manual_count = results.Sum(r => r.CheckResults.Count(c => c.Status == CheckStatuses.ManualRequired)),
                by_level = results
                    .GroupBy(r => "L" + r.Level)
                    .ToDictionary(
                        g => g.Key,
                        g => new
                        {
                            total = g.Count(),
                            pass_rate = g.Count(r => r.Pass) / (double)Math.Max(1, g.Count()),
                            strict_pass_rate = g.Count(r => r.StrictPass) / (double)Math.Max(1, g.Count())
                            ,coverage_rate = g.Sum(r => r.TotalExpectedPoints) <= 0 ? 0 : g.Sum(r => r.ScoredPoints) / g.Sum(r => r.TotalExpectedPoints)
                            ,unsupported_count = g.Sum(r => r.CheckResults.Count(c => c.Status == CheckStatuses.Unsupported))
                            ,manual_count = g.Sum(r => r.CheckResults.Count(c => c.Status == CheckStatuses.ManualRequired))
                        })
            };
        }

        private static void WriteTaskResults(string path, IReadOnlyList<CaseRunResult> results)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("case_id,level,variant,status,score,pass,strict_pass,safety_violation,coverage_rate,scored_points,total_expected_points,unsupported_points,manual_points,total_tokens,llm_calls,tool_calls,failed_tools,accurate_tools,duration_ms,output_docx");
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
                        result.CoverageRate.ToString(CultureInfo.InvariantCulture),
                        result.ScoredPoints.ToString(CultureInfo.InvariantCulture),
                        result.TotalExpectedPoints.ToString(CultureInfo.InvariantCulture),
                        result.UnsupportedPoints.ToString(CultureInfo.InvariantCulture),
                        result.ManualPoints.ToString(CultureInfo.InvariantCulture),
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
                writer.WriteLine("## Scoring Coverage");
                writer.WriteLine();
                writer.WriteLine("Coverage is the share of expected points handled by deterministic scorers. Unsupported and manual checks are excluded from the automatic score.");
                writer.WriteLine();
                writer.WriteLine("| Case | Level | Score | Coverage | Unsupported | Manual | Pass | Strict | Safety | Status |");
                writer.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | --- | --- | --- | --- |");
                foreach (var result in results)
                {
                    writer.WriteLine($"| {result.CaseId} | L{result.Level} | {result.Score.ToString(CultureInfo.InvariantCulture)} | {result.CoverageRate.ToString("P1", CultureInfo.InvariantCulture)} | {result.UnsupportedPoints.ToString(CultureInfo.InvariantCulture)} | {result.ManualPoints.ToString(CultureInfo.InvariantCulture)} | {result.Pass} | {result.StrictPass} | {result.SafetyViolation} | {result.Status} |");
                }

                writer.WriteLine();
                writer.WriteLine("## Check Details");
                writer.WriteLine();
                foreach (var result in results)
                {
                    foreach (var check in result.CheckResults.Where(c => c.Status != CheckStatuses.Passed))
                    {
                        writer.WriteLine("- " + result.CaseId + " / " + check.Type + " [" + check.Status + "]: " + check.Reason);
                    }
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
        public double TotalExpectedPoints { get; set; }
        public double ScoredPoints { get; set; }
        public double UnsupportedPoints { get; set; }
        public double ManualPoints { get; set; }
        public double CoverageRate { get; set; }
        public string OutputDocx { get; set; } = string.Empty;
        public string ScorePath { get; set; } = string.Empty;
        public string FailureReason { get; set; } = string.Empty;
        public string FailureDetail { get; set; } = string.Empty;
        public List<CheckResult> CheckResults { get; set; } = new List<CheckResult>();
    }
}
