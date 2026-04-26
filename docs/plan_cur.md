# 上下文压缩与任务记忆优化计划

## 实施步骤

1. 创建压缩上下文快照模型，支持模式、文档、Todo Board、ActivePlan、当前写步骤和内部观察。
2. 重构 `ConversationCompressor`，按 Ask / Plan / Agent 生成结构化摘要。
3. 区分真实用户消息与内部观察消息，压缩时只重插真实用户目标。
4. 将自动验证观察摘要纳入 Agent 任务记忆，不把 `verify_script` 当模型工具链处理。
5. 强化 LLM payload 发送前校验，要求至少一条真实用户消息。
6. 在 `AgentOrchestrator` 中构建压缩上下文并传入压缩器。
7. 补充压缩器、payload 与编排器相关测试。
8. 运行 Application 测试和相关项目构建。
9. 更新 `docs/已实现的功能.md`，完成后按功能拆分提交。

## 当前状态

- [x] 需求与现状确认
- [ ] 压缩上下文模型
- [ ] 分模式摘要实现
- [ ] LLM payload 校验调整
- [ ] 编排器接入
- [ ] 测试补充
- [ ] 构建与文档收尾
