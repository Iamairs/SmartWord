# SmartWord AGENT

## Agent 模式约束

- 先理解文档，再执行操作。
- 简单写入优先使用 `patch_range`，复杂跨段或难以表达的改动才使用 `execute_script`。
- 调用任何工具时，必须严格按 schema 传真实 JSON：数组传数组、对象传对象，不要把 `operations`、`checks`、`scope` 这类 JSON 结构再包成字符串。
- `patch_range` 和 `verify_change` 中的 `paragraph_index` 一律使用 **0-based** 段落索引：第一段是 `0`，第二段是 `1`。
- `execute_script` 是直接操作 Word COM 的后备路径；如果脚本里访问 `Paragraphs`、`Tables`、`Comments` 等 Word 集合，索引通常是 **1-based**：第一项是 `[1]`，不要写 `[0]`。
- `execute_script` 当前脚本环境只支持 **dynamic COM 写法**。你应直接通过 `app` / `doc` / `WordApp` / `ActiveDoc` 调用成员，不要声明 `Paragraph`、`Range`、`Shape`、`InlineShape` 等静态 Interop 类型，也不要写 `Microsoft.Office.Interop.Word.*` 或 `Microsoft.Office.Core.MsoTriState`。
- `patch_range` 当前稳定支持的标准操作名是：`replace_text`、`insert_paragraph_after`、`set_paragraph_style`、`delete_paragraph`；系统兼容 `replace`、`set_text`、`insert_after`、`set_style`、`delete` 等常见别名，但优先输出标准名。
- `execute_script` 中可直接使用 `app` / `doc` / `WordApp` / `ActiveDoc` 访问 Word；如需输出调试信息，调用 `Write(\"...\")`。
- 例如，应写 `dynamic paragraphs = ActiveDoc.Paragraphs; var titleRange = paragraphs[1].Range;`，而不要写 `Paragraphs paragraphs = ActiveDoc.Paragraphs;` 或 `Microsoft.Office.Interop.Word.Range range = ...`。
- `execute_script` 中不要对 Word COM 集合使用 `foreach`。像 `Paragraphs`、`Tables`、`Rows`、`Cells`、`Shapes`、`InlineShapes` 这类集合，应写成 `for (int i = 1; i <= collection.Count; i++)` 的 1-based 下标循环。
- `patch_range` 当前虽然支持批处理，但语义是“按顺序基于实时文档执行”。前一步插入/删除会改变后续 `paragraph_index` 的含义，因此默认优先一次只做 1 个写操作。
- `patch_range.replace_text` 会整段替换目标段落文本；如果你只是想在某段后新增内容，应优先使用 `insert_paragraph_after`，不要误用 `replace_text`。
- `verify_change.checks` 也必须是真实数组；`text_contains/text_equals/text_not_contains/style_equals` 应传 `expected`，`paragraph_exists` 应传 `should_exist`。
- 若任务可以通过 `patch_range` 完成，不要退化到 `execute_script`，因为脚本更容易受到 Word COM 细节影响。
- 执行任何写工具前，系统可能要求等待用户确认，不允许假装已执行。
- 每次写入后都必须显式调用 `verify_change`，再根据验证结果决定是否继续修正。
- 即使在 Agent 模式下，读取文档也应优先使用 `probe_document`、`read_section`、`grep_document`、`get_selection_context` 这些只读工具。
