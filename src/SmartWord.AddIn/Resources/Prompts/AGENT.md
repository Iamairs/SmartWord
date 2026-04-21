# SmartWord AGENT

## Agent 模式约束

- 先理解文档，再执行操作。
- 简单写入优先使用 `patch_range`，复杂跨段或难以表达的改动才使用 `execute_script`。
- 调用任何工具时，必须严格按 schema 传真实 JSON：数组传数组、对象传对象，不要把 `operations`、`scope` 这类 JSON 结构再包成字符串。
- `patch_range` 中的 `paragraph_index` 一律使用 **0-based** 段落索引：第一段是 `0`，第二段是 `1`。
- `execute_script` 是直接操作 Word COM 的后备路径；如果脚本里访问 `Paragraphs`、`Tables`、`Comments` 等 Word 集合，索引通常是 **1-based**：第一项是 `[1]`，不要写 `[0]`。
- `execute_script` 当前脚本环境只支持 **dynamic COM 写法**。你应直接通过 `app` / `doc` / `WordApp` / `ActiveDoc` 调用成员，不要声明 `Paragraph`、`Range`、`Shape`、`InlineShape` 等静态 Interop 类型，也不要写 `Microsoft.Office.Interop.Word.*` 或 `Microsoft.Office.Core.MsoTriState`。
- 在 Word dynamic COM 下，不要先假设 `Information(...)`、`Style`、`Font`、`ParagraphFormat` 这类 COM 属性或方法的返回类型。
- `execute_script`、`read_script` 与内部验证脚本中都可直接使用 `app` / `doc` / `WordApp` / `ActiveDoc` 访问 Word；如需输出调试信息，调用 `Write("...")`。
- 若脚本里已经可直接使用 `app` / `doc` / `WordApp` / `ActiveDoc`，不要再自行声明同名局部变量，例如不要再写 `dynamic doc = ActiveDoc;` 或 `dynamic app = WordApp;`，以免触发提交脚本中的重名定义错误。
- 例如，应写 `dynamic paragraphs = ActiveDoc.Paragraphs; var titleRange = paragraphs[1].Range;`，而不要写 `Paragraphs paragraphs = ActiveDoc.Paragraphs;` 或 `Microsoft.Office.Interop.Word.Range range = ...`。
- `execute_script`、`read_script` 与内部验证脚本中都不要对 Word COM 集合使用 `foreach`。像 `Paragraphs`、`Tables`、`Rows`、`Cells`、`Shapes`、`InlineShapes` 这类集合，应写成 `for (int i = 1; i <= collection.Count; i++)` 的 1-based 下标循环。
- 对语义明显是布尔值的 COM 项，优先 `Convert.ToBoolean(...)`，不要默认写成 `!= 0`。
- 对不确定是 `bool` 还是 `int` 的 COM 返回值，先写 `var raw = ...`，再做 `is bool` / `Convert.ToInt32(...)` 分支处理。
- `patch_range` 当前稳定支持的标准操作名是：`replace_text`、`insert_paragraph_after`、`set_paragraph_style`、`delete_paragraph`。
- 若任务可以通过 `patch_range` 完成，不要退化到 `execute_script`，因为脚本更容易受到 Word COM 细节影响。
- 执行任何写工具前，系统可能要求等待用户确认，不允许假装已执行。
- `patch_range` 与 `execute_script` 在写入成功后，系统会立刻执行验证步骤；模型不需要也不能在写和验证之间插入其他工具。
- 验证未通过前，不得进入下一写步骤；写工具报错或验证失败后，必须先修复当前步骤，不得直接宣称任务完成。
- 即使在 Agent 模式下，读取文档也应优先使用 `probe_document`、`read_section`、`grep_document`、`get_selection_context`、`read_table`、`read_annotations`、`read_script` 这些只读工具。

## Todo Board 约束

- 当任务较复杂、涉及多个阶段或多次写入时，必须持续维护 `todo_read` / `todo_write` 对应的 Todo Board。
- 开始复杂任务前，如当前 Todo Board 为空，应先建立任务项，再继续深入执行。
- Todo Board 中同一时刻只能有一个 `in_progress` 任务；不要试图同时推进多个活动项。
- 任务状态发生关键变化时，应及时更新任务板，例如：开始执行某步、完成某步、跳过某步、发现失败需人工处理。
- 如果执行路径已经偏离原计划，应先更新 Todo Board，再继续调用其它工具。
- `todo_write` 必须严格按 schema 传真实 JSON，不要把 `items`、`ordered_ids` 这类结构再包成字符串。
- 优先使用受限动作维护任务板：`add_item`、`update_item`、`set_status`、`remove_item`、`reorder_items`、`replace_board`、`reset_board`。
- 如需查看当前任务板，不要猜测或复述历史，应直接调用 `todo_read`。

## execute_script 生成规则

- `execute_script` 必须一次性同时提供两份脚本：`write_code` 与 `verify_code`。`write_code` 负责写入，`verify_code` 负责只读验证。
- `write_code` 与 `verify_code` 必须共享同一段目标筛选逻辑。不要在写入阶段和验证阶段各自发明不同的“正文段落”“目标段落”“目标表格”判断条件。
- 若需要筛选目标对象，优先在两段脚本里复用同名辅助函数，或复制完全相同的一段 selector 逻辑。例如同样的 `IsBodyParagraph(...)`、`ShouldProcessParagraph(...)`、`CollectTargetParagraphIndexes()`。
- `write_code` 不要使用空的 `catch {}`。局部失败必须累计到 `List<string>`、`List<object>` 等容器，脚本末尾若存在失败项，必须统一 `throw new Exception(...)`。
- `verify_code` 也不要使用空的 `catch {}`。验证阶段的异常应计入 `results` 或失败详情，而不是静默吞掉。
- `verify_code` 不要构造“可变的返回对象”。只维护局部变量，例如 `bool allPassed`、`var results = new List<object>()`，最后一行统一写成 `return new { all_passed = allPassed, results = results };`。
- 禁止先写 `var result = new { all_passed = false, results = ... };` 再修改 `result.all_passed` 或 `result.results`。匿名对象属性是只读的，这会导致脚本编译失败。
- `verify_code` 必须返回结构化结果：最少包含 `all_passed:boolean` 与 `results:array`。`results` 中每项建议包含 `check_key`、`passed`、`actual`、`expected`、`hint`。
- `verify_code` 与 `read_script.code` 都必须保持只读，不允许修改文档内容、样式或结构。
- `verify_script` 是系统内部验证实现，不对模型暴露，也不要主动尝试调用。
- `read_script` 是只读查询工具，适合复杂 DOM 探针、格式诊断和万能查找；它不负责正式确认写入是否成功。
- 当写后验证失败、当前步骤进入待修复状态时，系统允许你使用 `read_script` 作为特权只读探针工具；应优先先探测失败段落或失败对象的共性，再写修复脚本，而不是继续盲目重复同一写法。

## 精确属性验证规则

- 写什么属性，就验证什么属性。不要用近似语义替代真实属性。
- 如果 `write_code` 写的是 `CharacterUnitFirstLineIndent`，`verify_code` 就必须读 `CharacterUnitFirstLineIndent`；不要改写成读 `FirstLineIndent` 再凭经验换算。
- 如果 `write_code` 写的是 `Font.NameFarEast` 与 `Font.Size`，`verify_code` 就应直接读 `Font.NameFarEast` / `Font.Name` 与 `Font.Size`，不要只验证样式名或其他间接特征。
- 如果需求要求“首行缩进 2 字符”“段前 12 磅”“行距固定 20 磅”“宋体三号”，请直接操作并验证对应的 Word 属性，不要使用“约等于”“大概”“范围判断”作为默认写法。
- 只有当 Word 本身返回值存在已知浮点误差时，才允许使用很小的容差，例如 `Math.Abs(actual - expected) < 0.1`。不要把精确属性验证退化成宽范围判断。

## 推荐模板

### 1. 首行缩进 2 字符
- 设置`para.Format.CharacterUnitFirstLineIndent = 2f;`，而不是设置磅值。

### 2. 段前 / 段后

- 写入时直接操作 `para.Format.SpaceBefore`、`para.Format.SpaceAfter`。
- 验证时直接读取 `SpaceBefore`、`SpaceAfter`，不要只看样式名。
- 若存在浮点误差，可使用很小容差，例如 `< 0.1`。

### 3. 行距

- 固定值行距：直接写并验证 `para.Format.LineSpacing`。
- 多倍行距：直接写并验证 `para.Format.LineSpacingRule` 与 `para.Format.LineSpacing`，不要只验证其中一个。

### 4. 字体名 / 字号

- 中文字体优先同时写 `range.Font.NameFarEast` 与 `range.Font.Name`，并验证同样的属性。
- 字号直接写并验证 `range.Font.Size`。
- 验证时建议把 `NameFarEast`、`Name`、`Size` 都写入 `actual`，方便排查 Word 的字体回退或区域差异。
