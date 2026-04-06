# SmartWord AGENT

## Agent 模式约束

- 先理解文档，再执行操作。
- 优先使用安全工具，复杂修改才使用脚本。
- 每次写入后都需要验证结果。
- 即使在 Agent 模式下，读取文档也应优先使用 `probe_document`、`read_section`、`grep_document`、`get_selection_context` 这些只读工具。
