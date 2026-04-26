# 上下文压缩与任务记忆优化

## 需求背景

当前上下文压缩已能保证 provider 消息结构基本合法，但仍偏向消息裁剪，无法充分表达不同模式下的任务记忆。需要按 Ask、Plan、Agent 分模式生成摘要，并兼容 Todo Board 为空和 `verify_script` 内部化后的自动验证链路。

## 产品目标

- 压缩后必须保留真实用户目标，内部观察不能替代用户请求。
- Todo Board 是 Agent 复杂任务的状态来源之一，但为空时不能视为异常。
- Ask 和 Plan 模式不依赖 Todo Board，也不展示写入验证语义。
- Agent 模式优先保留写入安全状态、自动验证结论、已提交/回滚信息。
- `verify_script` 是系统内部质检执行器，不作为模型可见工具历史延续。

## 注意事项

- 不引入 LLM 二次总结，压缩保持本地、确定性、可测试。
- 不让 `ConversationCompressor` 直接访问 Todo 存储或 Office 对象。
- 不改变写后自动验证、失败回滚、文档只读保护等安全能力。
- 压缩后的 messages 必须继续满足 OpenAI-compatible tool calling 协议。
