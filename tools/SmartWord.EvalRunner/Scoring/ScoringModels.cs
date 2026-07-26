using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SmartWord.EvalRunner
{
    internal static class CheckStatuses
    {
        public const string Passed = "passed";
        public const string Failed = "failed";
        public const string Unsupported = "unsupported";
        public const string ManualRequired = "manual_required";
    }

    internal sealed class ScoreContext
    {
        public BenchmarkCase BenchmarkCase { get; set; }
        public JObject Check { get; set; }
        public DocxSnapshot Input { get; set; }
        public DocxSnapshot Output { get; set; }
        public IReadOnlyList<JObject> Trace { get; set; } = Array.Empty<JObject>();
    }

    internal interface ICheckScorer
    {
        IReadOnlyCollection<string> Types { get; }
        bool CanScore(string type);
        CheckResult Score(ScoreContext context);
    }

    internal sealed class ScoreResult
    {
        public string CaseId { get; set; } = string.Empty;
        public double Score { get; set; }
        public bool Pass { get; set; }
        public bool StrictPass { get; set; }
        public bool SafetyViolation { get; set; }
        public double TotalExpectedPoints { get; set; }
        public double ScoredPoints { get; set; }
        public double EarnedPoints { get; set; }
        public double UnsupportedPoints { get; set; }
        public double ManualPoints { get; set; }
        public double CoverageRate { get; set; }
        public List<string> UnsupportedChecks { get; set; } = new List<string>();
        public List<CheckResult> Checks { get; set; } = new List<CheckResult>();
    }

    internal sealed class CheckResult
    {
        public string Type { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Status { get; set; } = CheckStatuses.Unsupported;
        public bool Supported { get; set; }
        public double Points { get; set; }
        public double EarnedPoints { get; set; }
        public bool Passed { get; set; }
        public bool SafetyViolation { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string ExpectedSummary { get; set; } = string.Empty;
        public string ActualSummary { get; set; } = string.Empty;

        public static CheckResult Deterministic(
            string type,
            string category,
            double points,
            bool passed,
            string reason,
            string expected = "",
            string actual = "",
            bool safetyViolation = false)
        {
            return new CheckResult
            {
                Type = type,
                Category = category,
                Status = passed ? CheckStatuses.Passed : CheckStatuses.Failed,
                Supported = true,
                Points = points,
                EarnedPoints = passed ? points : 0,
                Passed = passed,
                SafetyViolation = safetyViolation,
                Reason = reason,
                ExpectedSummary = expected,
                ActualSummary = actual
            };
        }

        public static CheckResult Unsupported(string type, double points, string reason)
        {
            return new CheckResult
            {
                Type = type,
                Category = "unsupported",
                Status = CheckStatuses.Unsupported,
                Supported = false,
                Points = points,
                Reason = reason
            };
        }

        public static CheckResult Manual(string type, double points, string reason)
        {
            return new CheckResult
            {
                Type = type,
                Category = "semantic",
                Status = CheckStatuses.ManualRequired,
                Supported = false,
                Points = points,
                Reason = reason
            };
        }
    }
}
