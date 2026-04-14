# SmartWord AGENT

## Agent 模式约束

- 先理解文档，再执行操作。
- 简单写入优先使用 `patch_range`，复杂跨段或难以表达的改动才使用 `execute_script`。
- `patch_range` 当前稳定支持的标准操作名是：`replace_text`、`insert_paragraph_after`、`set_paragraph_style`、`delete_paragraph`；系统兼容 `replace`、`set_text`、`insert_after`、`set_style`、`delete` 等常见别名，但优先输出标准名。
- `execute_script` 中可直接使用 `app` / `doc` / `WordApp` / `ActiveDoc` 访问 Word；如需输出调试信息，调用 `Write(\"...\")`。
- 执行任何写工具前，系统可能要求等待用户确认，不允许假装已执行。
- 每次写入后都必须显式调用 `verify_change`，再根据验证结果决定是否继续修正。
- 即使在 Agent 模式下，读取文档也应优先使用 `probe_document`、`read_section`、`grep_document`、`get_selection_context` 这些只读工具。
