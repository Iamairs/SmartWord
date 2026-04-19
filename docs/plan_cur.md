Plan 模式卡住问题修复计划

## Step 1：补齐问题边界

- 阅读 `README.md`、`docs/已实现的功能.md`、`docs/instructions/Agent_核心引擎规格.md`
- 确认当前 Plan 模式设计目标、提示词约束、前端问题面板能力和宿主问答通道实现
- 根据日志把断点缩小到 `AgentOrchestrator` 收到 tool calls 之后的处理阶段

## Step 2：修复编排层协议一致性

- 在 `AgentOrchestrator` 中为缺失 `tool_call.id` 的调用生成稳定 ID
- 当同一轮 assistant 返回多个 tool call，但当前轮因采访等待或状态切换而提前结束时：
  - 将剩余 tool call 统一追加为 skipped tool result
  - 避免下一轮请求携带“assistant 有 3 个 tool_calls，但只有 1 个 tool result”的非法消息序列
- 为 `ask_user_question` 增加空问题文本保护，避免前端收到空问题后进入无反馈状态
- 增加关键日志，标明：
  - 本轮 tool call 摘要
  - Plan 问题已发出
  - 正在等待用户回答
  - 已收到用户回答

## Step 3：修复提示词与前端能力不一致

- 更新 `src/SmartWord.AddIn/Resources/Prompts/PLAN.md`
- 明确要求：
  - 单轮最多调用一次 `ask_user_question`
  - 如果还有其它必须澄清的问题，必须等待用户回答后再继续下一问
  - 整个采访阶段最多 3 轮

## Step 4：补充自动化测试

- 在 `tests/SmartWord.Application.Tests/Orchestration` 增加 Plan 模式测试
- 覆盖场景：
  - 第一轮返回多个 `ask_user_question` 时，剩余 tool call 会被自动补齐为 skipped result
  - 模型未提供 `tool_call.id` 时，`QuestionAsked` 事件仍能携带非空 ID

## Step 5：执行验证

- 运行 `tests/SmartWord.Application.Tests` 中与编排器相关的测试
- 如时间允许，运行完整应用层测试项目
- 最终汇总：
  - 根因
  - 修改点
  - 已验证范围
  - 宿主侧仍需人工 E2E 验证的部分
