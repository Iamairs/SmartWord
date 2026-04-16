# 当前需求说明

## 目标

将 Agent 写操作闭环从“写成功后进入待验证，模型可显式调用 `verify_script`，系统再兜底补验证”
改造为“写成功后编排层立即执行正式验证，不允许模型在写与验证之间插入任何工具”。

## 本次改造范围

- 保留模型可见写工具：
  - `patch_range`
  - `execute_script`
- `execute_script` 继续要求：
  - `write_code`
  - `verify_code`
- `verify_script` 改为内部实现工具，不再对模型暴露。
- 新增模型可见只读脚本查询工具：
  - `read_script`
- 编排层改为：
  - 写成功后立即验证
  - 验证结果作为同一步写步骤的后置结果回填
  - 只有验证通过才发 `ChangeApplied`
  - 验证失败直接进入 `ChangeVerificationFailed`
  - 模型下一轮只能继续下一步或修复当前失败步骤

## 关键约束

- `read_script` 只能执行读操作，禁止写入。
- `verify_script` 仍使用只读脚本执行，但仅供系统内部调用。
- 不再允许模型显式调用 `verify_script` 完成正式确认。
- 继续保持任务级 `UndoScope`、写失败修复态与回滚语义。

## 主要风险

- 需要收紧 `ToolRegistry` 的模型可见性，否则 `verify_script` 会继续暴露给模型。
- 需要重写 `AgentOrchestrator` 中现有“等待验证 / 自动补验证 / 显式验证工具”的分支，否则状态机会混乱。
- 需要补齐 `read_script` 的 schema、只读校验、序列化输出和测试，避免再次和正式验证语义混在一起。
