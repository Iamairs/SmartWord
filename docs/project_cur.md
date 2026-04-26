# 当前需求说明：自动验证结果改为内部观察消息

## 1. 背景

Agent 写入链路会在 `patch_range` / `execute_script` 成功后执行系统自动验证，确保写步骤只有在验证通过后才提交。此前自动验证结果通过 `AppendToolResultAsync` 写入对话历史，导致它被保存为 `role=tool` 消息。

这与 OpenAI 兼容工具协议冲突：`role=tool` 必须回应上一条模型生成的 `assistant.tool_calls`。自动验证是系统内部动作，不是模型主动发起的工具调用，因此会形成孤立 tool 消息，并在下一轮 LLM 请求前触发保护错误。

## 2. 当前需求

采用方案 A：自动验证结果作为 `role=user` 内部观察消息进入 LLM 上下文。

具体要求：

1. 模型真实发起的 `patch_range` / `execute_script` 工具结果继续按 `role=tool` 写入。
2. 系统自动验证结果不再调用 `AppendToolResultAsync`，不再产生 `__auto_verify` 的 tool 历史。
3. 自动验证通过时，向模型提示当前写步骤已验证通过并提交，要求继续后续 Todo。
4. 自动验证失败或验证工具执行失败时，向模型提供验证结论、验证输出和下一步修复要求。
5. 补充测试，确认下一轮 LLM messages 中没有孤立 `__auto_verify` tool 消息，且模型能看到失败原因。

## 3. 设计决策

- 内部观察使用 `role=user`，而不是中途插入 `role=system`，以兼容更多 OpenAI-compatible 服务。
- 不伪造 synthetic `assistant.tool_calls`，避免把系统内部动作伪装成模型行为。
- 观察消息保留结构化标题 `[SmartWord 自动验证结果]`，便于模型识别这不是用户新增需求。
- 验证输出做长度截断，避免异常堆栈或验证 JSON 过长污染上下文。

## 4. 交付范围

- 修改 `AgentOrchestrator.ExecuteAutoVerifyAsync` 的历史写入方式。
- 新增内部观察消息追加和自动验证观察消息构建逻辑。
- 补充 AgentOrchestrator 自动验证通过/失败的协议安全测试。
- 更新 `docs/plan_cur.md` 和 `docs/已实现的功能.md`。
