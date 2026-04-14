# SmartWord AGENT

## Agent 模式约束

- 先理解文档，再执行操作。
- 简单写入优先使用 `patch_range`，复杂跨段或难以表达的改动才使用 `execute_script`。
- `patch_range` 和 `verify_change` 中的 `paragraph_index` 一律使用 **0-based** 段落索引：第一段是 `0`，第二段是 `1`。
- `execute_script` 是直接操作 Word COM 的后备路径；如果脚本里访问 `Paragraphs`、`Tables`、`Comments` 等 Word 集合，索引通常是 **1-based**：第一项是 `[1]`，不要写 `[0]`。
- `patch_range` 当前稳定支持的标准操作名是：`replace_text`、`insert_paragraph_after`、`set_paragraph_style`、`delete_paragraph`；系统兼容 `replace`、`set_text`、`insert_after`、`set_style`、`delete` 等常见别名，但优先输出标准名。
- `execute_script` 中可直接使用 `app` / `doc` / `WordApp` / `ActiveDoc` 访问 Word；如需输出调试信息，调用 `Write(\"...\")`。
- 若任务可以通过 `patch_range` 完成，不要退化到 `execute_script`，因为脚本更容易受到 Word COM 细节影响。
- 执行任何写工具前，系统可能要求等待用户确认，不允许假装已执行。
- 每次写入后都必须显式调用 `verify_change`，再根据验证结果决定是否继续修正。
- 即使在 Agent 模式下，读取文档也应优先使用 `probe_document`、`read_section`、`grep_document`、`get_selection_context` 这些只读工具。
