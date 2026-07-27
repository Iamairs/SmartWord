namespace SmartWord.EvalRunner
{
    /// <summary>语义质量项默认交由人工复核，避免基础自动分引入不稳定判断。</summary>
    internal sealed class SemanticCheckScorer : CheckScorerBase
    {
        public SemanticCheckScorer()
            : base("semantic_quality_review")
        {
        }

        public override CheckResult Score(ScoreContext context)
        {
            return CheckResult.Manual(
                context.Check.Value<string>("type") ?? string.Empty,
                Points(context.Check),
                "语义质量评审需要人工或独立 judge_score，默认不计入基础自动分。");
        }
    }
}
