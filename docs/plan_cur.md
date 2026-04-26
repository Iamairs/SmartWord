# Todo List

- [x] P0 写入本轮需求说明和总体实施计划
- [x] P1 引入内部观察消息元数据并更新复制链路
- [ ] P2 重构自动验证观察写入时机：提交/回滚完成后再追加
- [ ] P3 调整自动验证观察内容：成功短消息、失败详细消息
- [ ] P4 确认并测试 `verify_script` 模型不可见但系统可调用
- [ ] P5 补强写工具验证计划契约文档和提示词
- [ ] P6 补充/更新自动化测试
- [ ] P7 运行后端测试与必要构建检查
- [ ] P8 更新 `docs/已实现的功能.md`
- [ ] P9 按子目标分多次精准提交 git

# 实施计划：验证托管与内部观察架构完善

## 1. 总体目标

把验证链路正式收敛为：

```text
模型提交写入方案 + 验证计划
系统执行写入
系统执行验证
系统提交或回滚当前步骤
系统把确定的验证观察反馈给模型
模型继续后续步骤或修复当前步骤
```

模型不直接操作 `verify_script`；`verify_script` 是系统内部只读验证执行器。

## 2. 子提交规划

### Commit 1：文档计划

- 写入 `docs/project_cur.md`。
- 写入 `docs/plan_cur.md`。

### Commit 2：内部观察元数据

- 在 `AgentMessage` 增加：
  - `IsInternalObservation`
  - `InternalObservationKind`
- 更新消息克隆路径：
  - `AgentOrchestrator.CloneMessage`
  - `InMemoryConversationStore.CloneMessage`
  - `ConversationCompressor.CloneMessage`
  - 测试里的 Fake store / Fake LLM clone。
- `AppendInternalObservationAsync` 设置元数据：
  - `Role = "user"`
  - `IsInternalObservation = true`
  - `InternalObservationKind = "auto_verify_result"`

### Commit 3：自动验证提交/回滚后观察

- `ExecuteAutoVerifyAsync` 只执行验证并返回 `AutoVerifyOutcome`，不直接追加观察。
- 自动验证通过：
  - 先提交写步骤 Undo。
  - 再追加短内部观察：该步骤已验证通过并提交，继续后续 Todo。
- 自动验证失败：
  - 先回滚当前写步骤。
  - 再追加详细内部观察：当前失败步骤已回退、验证结论、验证输出、修复要求。
- 无验证计划 / 验证工具不可用等失败也走详细观察。

### Commit 4：工具可见性与契约

- 确认 `VerifyScriptTool.IsVisibleToModel == false`。
- 增加或强化测试：Agent 模式工具定义不包含 `verify_script`，但编排器仍能内部执行 `verify_script`。
- 更新 AGENT prompt 和功能文档，明确：
  - 不主动调用 `verify_script`。
  - `execute_script` 必须提供 `write_code + verify_code`。
  - `patch_range` 默认由系统构建基础验证；复杂/不可靠验证场景应改用 `execute_script + verify_code`。

### Commit 5：测试与功能文档

- 更新自动验证通过/失败测试，断言：
  - 下一轮请求没有 `__auto_verify` tool。
  - 内部观察含 `IsInternalObservation=true`。
  - 成功观察简短且包含“已提交”。
  - 失败观察包含“已回退”和详细失败原因。
- 运行：
  - `dotnet test tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj --no-restore`
- 更新 `docs/已实现的功能.md`。

## 3. 验证要求

- 不放宽 `OpenAiCompatibleClient` 的 tool 协议校验。
- 不伪造 synthetic assistant tool call。
- 保证真实模型工具调用仍以 `assistant.tool_calls -> tool` 成对进入历史。
- 保证自动验证内部结果不再生成 `role=tool`。

## 4. 风险与边界

- 旧运行内存里已经存在的孤立 `__auto_verify` tool 消息无法靠本次代码自动清除；需要重启插件或清理当前会话。
- 本轮不改变前端展示内部观察的策略；内部观察主要用于 LLM 上下文，不作为普通用户气泡主动展示。
- 本轮不改变 Word 文档内容比对策略，只完善写后验证和状态反馈链路。
