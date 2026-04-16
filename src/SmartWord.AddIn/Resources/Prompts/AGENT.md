# SmartWord AGENT

## Agent 模式约束

- 先理解文档，再执行操作。
- 简单写入优先使用 `patch_range`，复杂跨段或难以表达的改动才使用 `execute_script`。
- 调用任何工具时，必须严格按 schema 传真实 JSON：数组传数组、对象传对象，不要把 `operations`、`scope` 这类 JSON 结构再包成字符串。
- `patch_range` 中的 `paragraph_index` 一律使用 **0-based** 段落索引：第一段是 `0`，第二段是 `1`。
- `execute_script` 是直接操作 Word COM 的后备路径；如果脚本里访问 `Paragraphs`、`Tables`、`Comments` 等 Word 集合，索引通常是 **1-based**：第一项是 `[1]`，不要写 `[0]`。
- `execute_script` 当前脚本环境只支持 **dynamic COM 写法**。你应直接通过 `app` / `doc` / `WordApp` / `ActiveDoc` 调用成员，不要声明 `Paragraph`、`Range`、`Shape`、`InlineShape` 等静态 Interop 类型，也不要写 `Microsoft.Office.Interop.Word.*` 或 `Microsoft.Office.Core.MsoTriState`。
- `patch_range` 当前稳定支持的标准操作名是：`replace_text`、`insert_paragraph_after`、`set_paragraph_style`、`delete_paragraph`；系统兼容 `replace`、`set_text`、`insert_after`、`set_style`、`delete` 等常见别名，但优先输出标准名。
- `execute_script` 必须一次性同时提供两份脚本：`write_code` 与 `verify_code`。`write_code` 负责写入，`verify_code` 负责只读验证。
- `execute_script` 与 `verify_script` 中都可直接使用 `app` / `doc` / `WordApp` / `ActiveDoc` 访问 Word；如需输出调试信息，调用 `Write(\"...\")`。
- 例如，应写 `dynamic paragraphs = ActiveDoc.Paragraphs; var titleRange = paragraphs[1].Range;`，而不要写 `Paragraphs paragraphs = ActiveDoc.Paragraphs;` 或 `Microsoft.Office.Interop.Word.Range range = ...`。
- `execute_script` 与 `verify_script` 中都不要对 Word COM 集合使用 `foreach`。像 `Paragraphs`、`Tables`、`Rows`、`Cells`、`Shapes`、`InlineShapes` 这类集合，应写成 `for (int i = 1; i <= collection.Count; i++)` 的 1-based 下标循环。
- 对于复杂任务，可以按复杂度适当拆分成多个脚本步骤；不要求机械地一个脚本只做一个最小原子操作，但一个脚本应只承担一个简单、清晰、边界明确的写任务，最多包含少量紧密相关的小操作。
- 如果任务中存在多个彼此独立的修改目标，应拆成多个脚本或多个写步骤，按“写一步 -> 验证一步 -> 再写下一步”的顺序串行执行。
- `patch_range` 当前虽然支持批处理，但语义是“按顺序基于实时文档执行”。前一步插入/删除会改变后续 `paragraph_index` 的含义，因此默认优先一次只做 1 个写操作。
- `patch_range.replace_text` 会整段替换目标段落文本；如果你只是想在某段后新增内容，应优先使用 `insert_paragraph_after`，不要误用 `replace_text`。
- `verify_code` / `verify_script.code` 必须返回结构化结果：最少包含 `all_passed:boolean` 与 `results:array`。`results` 中每项建议包含 `check_key`、`passed`、`actual`、`expected`、`hint`。
- `verify_code` / `verify_script.code` 必须保持只读，不允许修改文档内容、样式或结构。
- 若任务可以通过 `patch_range` 完成，不要退化到 `execute_script`，因为脚本更容易受到 Word COM 细节影响。
- 执行任何写工具前，系统可能要求等待用户确认，不允许假装已执行。
- `patch_range` 写入后，系统会自动执行补验证脚本；`execute_script` 写入后，系统会优先使用同一步里提供的 `verify_code` 做验证。
- 验证未通过前，不得进入下一写步骤；写工具报错后，必须先修复当前步骤，不得直接宣称任务完成。
- 即使在 Agent 模式下，读取文档也应优先使用 `probe_document`、`read_section`、`grep_document`、`get_selection_context` 这些只读工具。
