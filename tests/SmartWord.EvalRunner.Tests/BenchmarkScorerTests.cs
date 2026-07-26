using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SmartWord.EvalRunner.Tests
{
    public sealed class BenchmarkScorerTests
    {
        [Fact]
        public void Aggregate_包含非自动检查_仅按确定性检查计分()
        {
            var result = BenchmarkScorer.Aggregate("case", new[]
            {
                CheckResult.Deterministic("a", "text", 40, true, "通过"),
                CheckResult.Deterministic("b", "text", 20, false, "失败"),
                CheckResult.Unsupported("c", 30, "不支持"),
                CheckResult.Manual("d", 10, "人工")
            });

            Assert.Equal(60, result.ScoredPoints);
            Assert.Equal(40, result.EarnedPoints);
            Assert.Equal(100, result.TotalExpectedPoints);
            Assert.Equal(0.6, result.CoverageRate, 4);
            Assert.Equal(66.67, result.Score, 2);
            Assert.False(result.Pass);
            Assert.False(result.StrictPass);
        }

        [Fact]
        public void BenchmarkCases_全部检查类型_均已注册()
        {
            var casesRoot = Path.Combine(FindRepositoryRoot(), "benchmark", "cases");
            var types = Directory.EnumerateFiles(casesRoot, "expected.json", SearchOption.AllDirectories)
                .SelectMany(path => (JObject.Parse(File.ReadAllText(path))["checks"] as JArray ?? new JArray()).OfType<JObject>())
                .Select(check => check.Value<string>("type") ?? string.Empty)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var missing = types.Where(type => !BenchmarkScorer.RegisteredTypes.Contains(type, StringComparer.OrdinalIgnoreCase)).ToList();
            Assert.True(missing.Count == 0, "未注册 check type: " + string.Join(", ", missing));
        }

        [Fact]
        public void BenchmarkCases_DryScore_每个检查返回明确状态()
        {
            var root = FindRepositoryRoot();
            var casesRoot = Path.Combine(root, "benchmark", "cases");
            var tempDir = CreateTempDirectory();
            try
            {
                var docx = Path.Combine(tempDir, "fixture.docx");
                var trace = Path.Combine(tempDir, "trace.jsonl");
                CreateFixture(docx);
                File.WriteAllText(trace, string.Empty);
                var allowed = new HashSet<string>(new[] { CheckStatuses.Passed, CheckStatuses.Failed, CheckStatuses.Unsupported, CheckStatuses.ManualRequired }, StringComparer.OrdinalIgnoreCase);

                foreach (var expectedPath in Directory.EnumerateFiles(casesRoot, "expected.json", SearchOption.AllDirectories))
                {
                    var benchmarkCase = new BenchmarkCase { Id = Path.GetFileName(Path.GetDirectoryName(expectedPath)), ExpectedJsonPath = expectedPath };
                    var score = BenchmarkScorer.Score(benchmarkCase, docx, docx, trace);
                    Assert.All(score.Checks, check => Assert.Contains(check.Status, allowed));
                    Assert.DoesNotContain(score.Checks, check => check.Reason.IndexOf("未注册", StringComparison.OrdinalIgnoreCase) >= 0 || check.Reason.IndexOf("尚未实现", StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (current != null && !File.Exists(Path.Combine(current.FullName, "SmartWord.sln"))) current = current.Parent;
            Assert.NotNull(current);
            return current.FullName;
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "SmartWord.EvalRunner.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void CreateFixture(string path)
        {
            using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Text("测试标题"))),
                    new Table(
                        new TableProperties(new TableBorders(new TopBorder { Val = BorderValues.Single })),
                        new TableRow(new TableRowProperties(new TableHeader()), new TableCell(new Paragraph(new Run(new Text("表头"))))),
                        new TableRow(new TableCell(new Paragraph(new Run(new Text("内容")))))),
                    new SectionProperties(
                        new PageSize { Width = 11906U, Height = 16838U },
                        new PageMargin { Top = 1440, Bottom = 1440, Left = 1440U, Right = 1440U })));
                main.Document.Save();
            }
        }
    }
}
