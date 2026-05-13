using System;
using System.Collections.Generic;
using System.IO;

namespace SmartWord.EvalRunner
{
    internal sealed class EvalRunnerOptions
    {
        public string CasesRoot { get; set; } = Path.Combine("benchmark", "cases");
        public string CaseId { get; set; } = string.Empty;
        public int Level { get; set; }
        public string Variant { get; set; } = "smartword";
        public string Model { get; set; } = Environment.GetEnvironmentVariable("SMARTWORD_EVAL_MODEL") ?? "gpt-4.1";
        public string BaseUrl { get; set; } = Environment.GetEnvironmentVariable("SMARTWORD_EVAL_BASE_URL") ?? "https://api.openai.com/v1";
        public string ApiKey { get; set; } = Environment.GetEnvironmentVariable("SMARTWORD_EVAL_API_KEY") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        public double Temperature { get; set; }
        public string Permission { get; set; } = "ConfirmWrites";
        public int MaxCases { get; set; }
        public int MaxIterations { get; set; } = 100;
        public int TimeoutSeconds { get; set; } = 120;
        public string Output { get; set; } = Path.Combine("benchmark", "runs", DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + "-smartword");
        public bool KeepWordVisible { get; set; }
        public string AutoConfirmPolicy { get; set; } = "approve_required";
        public bool ShowHelp { get; set; }

        public static string HelpText =>
@"SmartWord.EvalRunner
  --cases <benchmark/cases>
  --case-id <id>
  --level <1|2|3|4>
  --variant <smartword|baseline>
  --model <real-model>
  --base-url <openai-compatible-base-url>
  --api-key <key>
  --temperature <0>
  --permission <ConfirmWrites|ReadOnly|AutoSafeWrites|FullAuto>
  --max-cases <n>
  --output <benchmark/runs/run-id>
  --keep-word-visible
  --auto-confirm-policy <approve_required|approve_all|reject_all>";

        public static EvalRunnerOptions Parse(string[] args)
        {
            var options = new EvalRunnerOptions();
            var values = new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["--cases"] = v => options.CasesRoot = v,
                ["--case-id"] = v => options.CaseId = v,
                ["--level"] = v => options.Level = int.TryParse(v, out var level) ? level : 0,
                ["--variant"] = v => options.Variant = v,
                ["--model"] = v => options.Model = v,
                ["--base-url"] = v => options.BaseUrl = v,
                ["--api-key"] = v => options.ApiKey = v,
                ["--temperature"] = v => options.Temperature = double.TryParse(v, out var t) ? t : 0,
                ["--permission"] = v => options.Permission = v,
                ["--max-cases"] = v => options.MaxCases = int.TryParse(v, out var n) ? n : 0,
                ["--max-iterations"] = v => options.MaxIterations = int.TryParse(v, out var n) ? n : 100,
                ["--timeout-seconds"] = v => options.TimeoutSeconds = int.TryParse(v, out var n) ? n : 120,
                ["--output"] = v => options.Output = v,
                ["--auto-confirm-policy"] = v => options.AutoConfirmPolicy = v
            };

            for (var i = 0; i < (args == null ? 0 : args.Length); i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase))
                {
                    options.ShowHelp = true;
                    continue;
                }

                if (string.Equals(arg, "--keep-word-visible", StringComparison.OrdinalIgnoreCase))
                {
                    options.KeepWordVisible = true;
                    continue;
                }

                if (values.TryGetValue(arg, out var setter) && i + 1 < args.Length)
                {
                    setter(args[++i]);
                }
            }

            options.CasesRoot = Path.GetFullPath(options.CasesRoot);
            options.Output = Path.GetFullPath(options.Output);
            return options;
        }
    }
}
