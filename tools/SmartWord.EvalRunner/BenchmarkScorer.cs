using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SmartWord.EvalRunner
{
    internal static class BenchmarkScorer
    {
        private static readonly IReadOnlyList<ICheckScorer> Scorers = new ICheckScorer[]
        {
            new TextCheckScorer(),
            new TableCheckScorer(),
            new FormatCheckScorer(),
            new TraceCheckScorer(),
            new SemanticCheckScorer()
        };

        public static ScoreResult Score(
            BenchmarkCase benchmarkCase,
            string inputDocx,
            string outputDocx,
            string traceJsonl)
        {
            var expected = JObject.Parse(File.ReadAllText(benchmarkCase.ExpectedJsonPath));
            var input = DocxSnapshot.Load(inputDocx);
            var output = DocxSnapshot.Load(outputDocx);
            var trace = TraceReader.Read(traceJsonl, benchmarkCase.Id);
            var checks = new List<CheckResult>();

            foreach (var token in expected["checks"] as JArray ?? new JArray())
            {
                var check = token as JObject ?? new JObject();
                var type = check.Value<string>("type") ?? string.Empty;
                var scorer = Scorers.FirstOrDefault(item => item.CanScore(type));
                if (scorer == null)
                {
                    checks.Add(CheckResult.Unsupported(type, check.Value<double?>("points") ?? 0, "未注册该检查类型。"));
                    continue;
                }

                try
                {
                    checks.Add(scorer.Score(new ScoreContext
                    {
                        BenchmarkCase = benchmarkCase,
                        Check = check,
                        Input = input,
                        Output = output,
                        Trace = trace
                    }));
                }
                catch (Exception ex)
                {
                    checks.Add(CheckResult.Unsupported(type, check.Value<double?>("points") ?? 0, "检查字段无法稳定自动解析：" + ex.Message));
                }
            }

            return Aggregate(benchmarkCase.Id, checks);
        }

        internal static ScoreResult Aggregate(string caseId, IReadOnlyList<CheckResult> checks)
        {
            var totalExpectedPoints = checks.Sum(item => item.Points);
            var scoredPoints = checks.Where(item => item.Supported).Sum(item => item.Points);
            var earnedPoints = checks.Where(item => item.Supported).Sum(item => item.EarnedPoints);
            var unsupportedPoints = checks.Where(item => item.Status == CheckStatuses.Unsupported).Sum(item => item.Points);
            var manualPoints = checks.Where(item => item.Status == CheckStatuses.ManualRequired).Sum(item => item.Points);
            var safetyViolation = checks.Any(item => item.SafetyViolation);
            var score = scoredPoints <= 0 ? 0 : Math.Round(earnedPoints / scoredPoints * 100.0, 2);
            var coverageRate = totalExpectedPoints <= 0 ? 0 : Math.Round(scoredPoints / totalExpectedPoints, 4);

            return new ScoreResult
            {
                CaseId = caseId,
                Score = score,
                Pass = scoredPoints > 0 && score >= 80 && !safetyViolation,
                StrictPass = checks.Count > 0
                    && checks.All(item => item.Supported && item.Passed)
                    && !safetyViolation,
                SafetyViolation = safetyViolation,
                TotalExpectedPoints = totalExpectedPoints,
                ScoredPoints = scoredPoints,
                EarnedPoints = earnedPoints,
                UnsupportedPoints = unsupportedPoints,
                ManualPoints = manualPoints,
                CoverageRate = coverageRate,
                UnsupportedChecks = checks
                    .Where(item => !item.Supported)
                    .Select(item => item.Type)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Checks = checks.ToList()
            };
        }

        internal static IReadOnlyCollection<string> RegisteredTypes => Scorers
            .SelectMany(item => item.Types)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
