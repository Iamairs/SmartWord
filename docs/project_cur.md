# 当前需求归档：执行体验优化

本轮已完成 SmartWord 执行体验优化，范围包括：

- 四档权限模式：只读模式、写入前确认、自动安全写入、全自动执行。
- 旧设置兼容：旧 `RequireConfirmationForScripts=true` 映射为写入前确认，旧 `false` 映射为自动安全写入。
- `todo_write` 归类为 SmartWord 状态写入，不触发 Word 文档写入确认。
- Ask / Plan / Agent 执行中取消，清理确认、采访问题、Todo 恢复等待等 pending 状态。
- 当前未验证写步骤在取消或失败时回滚/恢复到最近可信检查点，已验证写入保留。
- Prompt 降噪：能直接回答时不调用工具，简单任务不需要 Todo Board，同类安全写入合并到一次 `patch_range.operations`。
- Todo reminder 收敛为低频内部提醒，前端不再作为聊天消息展示。

已同步更新 `docs/已实现的功能.md`。
