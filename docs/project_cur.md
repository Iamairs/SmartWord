# 当前需求：真实 Word + 真实 LLM Benchmark 执行与指标采集

## 需求背景

SmartWord 需要一个可复现的端到端评测闭环，用真实 Word、真实 LLM、真实 Agent Runtime 和真实 Office 工具链执行 benchmark case，并将任务、LLM、工具、确认、验证、上下文压缩和判分结果全部落盘。评测结果用于量化任务完成率、token 花费、API 耗时、工具耗时、工具失败率、工具准确率和安全违规率。

## 核心原则

1. 正式指标只来自真实执行日志和离线 Scorer，不把 Runtime 的 `TaskCompleted` 直接当完成率。
2. 评测数据库必须独立于用户正式 `smartword.db`，避免污染插件历史。
3. JSONL trace 用于人工复盘，SQLite 用于统计查询，case 目录保留 `input.docx`、`output.docx`、`task.json`、`expected.json`、`score.json`。
4. token 优先记录真实 API usage；provider 未返回 usage 时，只记录 estimated 字段并在报告中保持可区分。
5. EvalRunner 可以自动确认 benchmark 写入请求，但不能绕过权限系统，确认过程必须记录。

## 当前实现范围

本轮先落地最小可编译闭环：

- Core 增加 Telemetry 事件模型、sink 接口和 LLM usage 元数据。
- Infrastructure 增加 JSONL sink、SQLite eval sink、Composite sink 和 Telemetry LLM wrapper。
- AgentOrchestrator 在任务、权限、确认、工具执行、自动验证、上下文压缩处写事实事件。
- OpenAI compatible client 解析流式响应中可能返回的 usage / finish_reason / trace id。
- 新增 `tools/SmartWord.EvalRunner`，支持真实 Word COM 打开 case 文档、运行真实 Runtime、保存输出、调用 Scorer、生成报告。
- Scorer 先支持文本出现次数、工具必须/禁止调用、确认、写后验证、只读安全、文本保留和表格读取等过程类检查；复杂格式类检查保留为未实现的可追溯项。
- 新增 telemetry 单元测试覆盖 JSONL、SQLite 和 LLM wrapper。

## 注意事项

- 当前 EvalRunner 需要本机安装 Microsoft Word。
- 真实 LLM API Key 通过 `--api-key`、`SMARTWORD_EVAL_API_KEY` 或 `OPENAI_API_KEY` 提供。
- 如果 provider 不返回 usage，`TelemetryLlmClient` 会写入 estimated token，不能作为真实 token 指标宣传。
- 当前 scorer 还不是完整 OpenXML/COM 格式判分器，L1/L2 的样式、目录、页眉页脚、表格边框等仍需后续增强。
- 提交时按原子化提交执行，避免把非本轮的 benchmark 种子集或已有未跟踪文档混入不相关提交。
