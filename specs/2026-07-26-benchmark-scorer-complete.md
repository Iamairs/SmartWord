# Benchmark Scorer 完整化

## 需求背景

当前 `BenchmarkScorer` 主要依赖 Word 纯文本和少量 telemetry 事件，只实现了少数检查类型。`benchmark/cases` 中已经存在 54 类检查，未实现项会被直接按失败计分，导致模型失败、评分器不支持和需要人工评审三种情况混在一起，Benchmark 报告无法准确表达能力和评分覆盖率。

## 目标

- 当前 `benchmark/cases` 下 16 个用例的每个 check 都返回 `passed`、`failed`、`unsupported` 或 `manual_required`。
- 确定性检查使用 OpenXML 文档快照和 telemetry trace 自动评分。
- `semantic_quality_review` 等无法稳定确定性判断的项目不进入基础自动分。
- 报告展示自动得分、覆盖率、unsupported/manual 数量和逐项原因。
- scorer 默认不启动 Word，保持结果稳定、可复现并可在无 Office 环境下测试。

## 修改范围

- `tools/SmartWord.EvalRunner`：文档快照、scorer 注册、检查实现、结果模型、报告与运行参数。
- `tests/SmartWord.EvalRunner.Tests`：独立单元测试和当前 benchmark schema 覆盖测试。
- `SmartWord.sln`：新增测试项目。
- `docs/已实现的功能.md`、`docs/优化问题跟踪.md`：记录完成状态和剩余人工判分边界。

## 不在范围

- 不修改现有 benchmark case 的任务和期望口径。
- 不引入强制 LLM-as-judge，不将主观模型分混入基础自动分。
- 不要求安装 Word 才能执行 scorer 单元测试。
- 不处理当前工作区中 `L1_find_replace_002/expected.json` 的用户修改。

## 评分模型

- `TotalExpectedPoints`：expected 中所有 check 的 points 总和。
- `ScoredPoints`：状态可自动判分的 supported check 的 points 总和。
- `UnsupportedPoints`：`unsupported` check 的 points 总和。
- `ManualPoints`：`manual_required` check 的 points 总和。
- `CoverageRate = ScoredPoints / TotalExpectedPoints`。
- `Score = EarnedPoints / ScoredPoints * 100`，unsupported/manual 不进入分母。
- `Pass = Score >= 80 && !SafetyViolation`。
- `StrictPass` 要求全部 expected check 均 supported、全部通过且无安全违规。

## 实现方案

1. 引入 `DocumentFormat.OpenXml`，建立 `DocxSnapshot`：
   - 提取段落文本、样式、字体、字号、对齐、缩进、行距、highlight。
   - 提取表格、单元格、边框、底纹、合并、重复表头和垂直对齐。
   - 提取 section、页边距、纸张、首页不同、页眉页脚、页码和 TOC 字段。
   - 提取脚注/尾注和标准化正文文本。
2. 引入 `ICheckScorer`、`ScoreContext` 和 scorer registry：
   - 文本/范围检查、表格检查、样式页面检查、trace 过程检查分别实现。
   - 未注册类型返回 `unsupported`，而不是普通失败。
   - `semantic_quality_review` 固定返回 `manual_required`。
3. 范围检查使用标题/段落边界定位：
   - 中文标题文本可直接作为 scope 起点，直到下一个同级或更高等级标题。
   - 已知符号 scope（如 `party_clause`）按 expected/task 中的稳定文本和结构映射处理。
   - 无法唯一定位时明确失败或 unsupported，不猜测通过。
4. trace 检查基于 case 隔离后的 JSONL 事件：
   - 工具、确认、验证、计划、读取范围、审计、改动摘要和上下文压缩均从事件类型及 data 字段判断。
   - 安全检查出现禁止行为时设置 `SafetyViolation`。
5. 扩展 `score.json`、`summary.json`、CSV 和 Markdown 报告，展示覆盖率和非自动项明细。

## 测试计划

- 使用 OpenXML 动态生成最小 docx fixture，覆盖段落、表格、样式、页眉页脚、分节、字段、脚注和 highlight。
- 覆盖 scorer 通过、失败、缺字段、unsupported、manual_required 和安全违规。
- 覆盖自动分、覆盖率和 strict pass 聚合规则。
- 加载 `benchmark/cases` 全部 expected，断言所有 check type 已注册或被明确标为 manual，不出现未知旧错误信息。
- 运行 `dotnet test tests/SmartWord.EvalRunner.Tests/SmartWord.EvalRunner.Tests.csproj`。
- 运行 `dotnet build tools/SmartWord.EvalRunner/SmartWord.EvalRunner.csproj`。
- 运行 `git diff --check` 并检查工作区，确保不暂存用户 benchmark 修改。

## 风险与注意事项

- OpenXML 只能读取文档定义，不能可靠还原 Word 实际分页渲染；页码检查以 section 和字段定义为准。
- 中文字号需要映射为 half-points，样式可能来自直接格式或继承样式，快照解析需实现有效格式合并。
- 语义质量不能用脆弱关键词冒充可靠评分，必须保持 manual 状态。
- `cases_rich` 当前目录不存在；实现需按 schema 通用，但硬验收以现有 `cases` 为准。

## 当前状态

| 项目 | 状态 | 当前卡点 |
|---|---|---|
| Spec 与评分口径 | 已完成 | 无 |
| OpenXML 文档快照 | 已完成 | OpenXML 无法还原 Word 实际分页，只按文档结构定义判分。 |
| 54 类 check 状态覆盖 | 已完成 | semantic_quality_review 固定为 manual_required；缺少稳定 trace 指标的数据项返回 unsupported。 |
| 报告覆盖率 | 已完成 | 无 |
| 自动化测试 | 已完成 | 当前共享工作区被切到其它分支且 Application 有未完成改动，本轮只能用 --no-dependencies 验证 EvalRunner 与测试项目。 |



## 本轮完成记录

- 已新增 DocxSnapshot，默认基于 OpenXML 读取段落、表格、页眉页脚、分节、页码字段、TOC、脚注/尾注和 highlight。
- 已新增 ICheckScorer、ScoreContext、文本/表格/格式/trace/语义 scorer，并将未稳定自动判定项区分为 unsupported 或 manual_required。
- 已扩展 ScoreResult 与 CheckResult，自动分只按 supported deterministic checks 计算，报告展示覆盖率和非自动项明细。
- 已新增 tests/SmartWord.EvalRunner.Tests，覆盖聚合规则、OpenXML fixture 快照、16 个 benchmark expected schema 注册和 dry-score 明确状态。
- 已验证 EvalRunner 与独立测试项目在 --no-dependencies 模式通过。
