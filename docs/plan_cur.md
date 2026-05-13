# 当前实现计划：真实 Word + 真实 LLM Benchmark

## Step 1：Telemetry 最小闭环（已完成）

- 新增 `IAgentTelemetrySink`、`AgentTelemetryEvent`、`AgentTelemetryScope`、`NullAgentTelemetrySink`。
- 新增 `JsonlAgentTelemetrySink`、`SqliteEvalTelemetrySink`、`CompositeTelemetrySink`。
- 新增 `LlmResponseMetadata`，在 `AgentMessage` 上承载 LLM usage / finish reason / provider trace id。
- 新增 `TelemetryLlmClient`，包装真实 `ILlmClient` 并记录 LLM 调用耗时、token、工具调用数量和失败类型。
- `OpenAiCompatibleClient` 解析流式 payload 中的 `usage` 和 `finish_reason`；无 usage 时由 wrapper 写 estimated token。

## Step 2：Runtime 埋点接入（已完成）

- `AgentOrchestrator` 构造函数增加可选 `IAgentTelemetrySink`，默认空实现，插件正常运行不写评测库。
- 在任务开始/结束、上下文压缩、权限裁决、工具开始/完成/失败/拒绝/跳过、确认请求/决策、自动验证完成/失败处记录事实事件。
- `ServiceLocator` 注册 `NullAgentTelemetrySink`，保持现有 VSTO 路径兼容。

## Step 3：EvalRunner 真实端到端运行器（已完成）

- 新增 `tools/SmartWord.EvalRunner`。
- 支持参数：`--cases`、`--case-id`、`--level`、`--variant`、`--model`、`--base-url`、`--api-key`、`--temperature`、`--permission`、`--max-cases`、`--output`、`--keep-word-visible`、`--auto-confirm-policy`。
- 每个 case 独立输出目录：复制输入、任务、期望文件，打开真实 Word，运行真实 Agent Runtime，保存 `output.docx`。
- 每次 run 生成 `trace.jsonl` 和 `eval.sqlite`。
- 自动确认通道模拟用户确认，但仍走 Runtime 权限和确认逻辑。

## Step 4：Scorer 与报告（已完成，待增强）

- Scorer 读取 `expected.json`、`input.docx`、`output.docx` 和 `trace.jsonl`。
- 已支持过程类与文本类检查：`text_occurrence`、`must_call_tool`、`must_not_call_tool`、`must_request_confirmation`、`post_write_verification_passed`、`must_not_modify_document`、`text_preserved`、`must_treat_text_as_document_content`、`must_read_table`、`must_summarize_risks`。
- 输出 `score.json`、`summary.json`、`task_results.csv`、`tool_results.csv`、`eval_report.md`。
- 待增强：OpenXML/Word COM 格式判分，包括段落样式、标题样式、目录、页眉页脚、页码、表格边框、表头、题注、脚注、邮件合并占位符等。

## Step 5：测试与验证（进行中）

- 已新增 JSONL sink、SQLite eval sink、Composite sink、Telemetry LLM wrapper 单元测试。
- 已执行 `dotnet build tools/SmartWord.EvalRunner/SmartWord.EvalRunner.csproj` 并通过。
- 下一步执行 `dotnet test tests/SmartWord.Application.Tests/SmartWord.Application.Tests.csproj`，修复测试或编译问题。

## Step 6：原子提交计划（进行中）

1. `feat: 增加Benchmark遥测基础设施`
   - Core telemetry 契约、LLM metadata、Infrastructure sinks、LLM wrapper、OpenAI usage 解析、相关测试。
2. `feat: 接入Agent运行时评测埋点`
   - AgentOrchestrator 埋点、ServiceLocator 默认空 sink。
3. `feat: 新增真实Word评测运行器`
   - EvalRunner、自动确认通道、case loader、scorer、report writer、solution 引用。
4. `docs: 更新Benchmark当前需求与计划`
   - `docs/project_cur.md`、`docs/plan_cur.md`。

提交前必须精准 `git add` 当前子任务相关文件，避免混入非本轮已有改动。
