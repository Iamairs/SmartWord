# 当前实现计划

## Step 1：文档与协议收口

- 记录新执行协议：写工具成功后，编排层立即执行内部验证。
- 明确 `verify_script` 仅供内部调用。
- 明确 `read_script` 是新的模型可见只读脚本查询工具。

## Step 2：工具层改造

- 为工具增加“是否对模型可见”元数据。
- 更新 `ToolRegistry.GetToolDefinitions(...)`，隐藏内部工具。
- 新增 `ReadScriptTool`：
  - 只读权限
  - 使用 Roslyn 脚本执行
  - 复用 `ScriptValidationMode.ReadOnly`
  - 输出通用查询结果
- 保留 `VerifyScriptTool` 内部契约不变，但默认隐藏给模型。

## Step 3：编排层状态机改造

- 删除“写成功后等待模型显式验证”的开放窗口。
- 删除“下一工具不是验证工具时再自动补验证”的逻辑。
- 删除“对话结束时还有 AwaitingVerification 再补验证”的逻辑。
- 改为：
  - 写成功
  - 立刻执行验证子步骤
  - 立即产出 `ChangeApplied` 或 `ChangeVerificationFailed`
- 保留待修复状态，用于写失败或验证失败后的下一轮修复。

## Step 4：提示词与测试

- 更新 `AGENT.md`，移除模型显式调用 `verify_script` 的描述。
- 加入 `read_script` 的使用约束。
- 替换 `AgentOrchestratorPhase3Tests` 中所有显式 `verify_script` 流程为“系统立即验证”。
- 增加 `read_script` 的 schema / 输入校验 / 只读校验测试。
- 增加 `ToolRegistry` 的内部工具隐藏测试。

## Step 5：收尾

- 更新 `docs/已实现的功能.md` 中写后验证闭环与工具清单描述。
- 跑测试与构建。
- 完成后清空 `docs/project_cur.md` 与 `docs/plan_cur.md`。
