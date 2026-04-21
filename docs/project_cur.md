# 当前需求说明：Todo Board 任务板体系落地

## 1. 背景

当前 SmartWord 已具备 Ask / Plan / Agent 三种模式的基础编排能力，其中：

- Plan 模式已支持采访用户并输出结构化 `ExecutionPlan`
- 前端已支持展示 Plan 结果面板
- Agent 模式已支持工具调用、写入验证、宿主确认与前端事件回推

但当前计划能力仍停留在“生成计划并展示”的阶段，尚未形成一套可持续维护、可实时更新、可被模型自监督消费的任务板机制。现有 `ExecutionPlan` 结构、`ActivePlan` 字段、`ProgressUpdate` 事件均存在预留痕迹，但没有真正串成完整闭环。

## 2. 当前需求

本次需要在不接入数据库的前提下，补齐一版相对完善的 Todo Board 机制，而不是只做 MVP。整体目标如下：

1. 为复杂任务提供结构化任务板，而不再仅依赖模型在自然语言上下文中“记住计划”。
2. 让模型能够通过工具主动读取和更新任务板，形成可观察、可校验的自监督闭环。
3. 让系统能够在长会话中持续提醒模型维护任务板，避免计划漂移。
4. 让前端在 Agent 执行阶段实时看到任务板状态变化，而不是只看到普通消息流。
5. 先采用文件方案完成权威存储，后续保留接入 SQLite 的扩展点。

## 3. 已确认的关键决策

### 3.1 权威存储

- 采用 **JSON 文件** 作为 Todo Board 的唯一真相源。
- Markdown 不作为权威存储，仅作为工具反馈、调试视图和可读展示结果。

### 3.2 前端展示范围

- Todo Board 前端面板仅在 **Agent 执行阶段** 展示。
- Plan 阶段仍保留当前现有的计划确认面板，不直接替换为任务板。

## 4. 期望包含的核心能力

### 4.1 Todo 工具体系

至少需要新增：

- `TodoRead`：读取当前任务板
- `TodoWrite`：对任务板执行结构化更新

并由 `TodoManager` 统一维护约束，而不是把规则散落在工具或编排器里。

### 4.2 TodoManager 规则

任务板至少满足以下规则：

- 整个任务板最多 20 条任务
- 同一时刻只允许 1 条任务处于 `in_progress`
- 拒绝空任务、重复 id、非法状态、非法顺序
- 返回统计信息与 Markdown 任务板视图

### 4.3 编排器闭环

编排器需要补齐以下行为：

- 模型调用 Todo 工具后，将结果写回对话历史
- 让模型在下一轮能看到自己刚刚更新后的“计划板”
- 每次成功更新 Todo 后重置 reminder 计数
- 连续 10 轮未触发 Todo 时注入温和的 system reminder

### 4.4 前端实时同步

前端需要具备：

- Agent 执行阶段的独立 Todo Board 面板
- 接收后端 Todo 相关事件并整板刷新
- 展示当前任务、状态统计和更新时间

## 5. 当前仓库中的相关现状

### 5.1 已存在的基础能力

- `ExecutionPlan` 已定义 `TodoList` 与状态字段雏形
- `AgentRunOptions` 已预留 `ActivePlan`
- `AgentEventType` 已预留 `ProgressUpdate`
- `WebViewBridge` 已具备统一事件推送机制
- 前端 `ChatWindow` / `chatStore` 已具备接收事件并更新状态的模式

### 5.2 当前缺失点

- 没有真正的 Todo 权威存储
- 没有 Todo 工具
- 没有 Todo 业务管理器
- 没有 Todo reminder 机制
- 没有 Agent 执行阶段的 Todo Board 面板
- 没有面向 Todo 的完整自动化测试

## 6. 设计约束与注意事项

### 6.1 技术约束

- 项目后端为 `.NET Framework 4.7.2`
- 现有工程采用经典 csproj 和分层结构
- 文档均使用 UTF-8 编码
- 代码注释需使用中文

### 6.2 架构约束

- 业务规则优先落在 Application / Core 层，不应把复杂约束写进 AddIn 宿主层
- 前端不负责推断 Todo 状态，只消费后端下发的权威快照
- Todo 工具不能允许模型自由编辑 Markdown 文件，以免污染表示层
- 需要尽量复用现有工具注册、工具结果回填、前端事件分发链路

### 6.3 交付约束

- 本轮先不接数据库
- 需要先完成 JSON 文件方案
- 后续可平滑扩展到 SQLite，因此接口设计需要预留存储抽象

## 7. 本轮交付目标

本轮要完成一版可直接进入实现的详细方案，并按方案逐步落地：

1. 建立 Todo Board 的模型、存储、业务管理器和工具
2. 把 Todo 能力接入 Agent 编排主循环
3. 完成前端 Todo Board 面板与事件联动
4. 完成关键自动化测试
5. 在功能完成后补充更新 `docs/已实现的功能.md`

## 8. 当前实现结果摘要

当前 Todo Board 方案已按上述目标完成首版落地，核心结果如下：

- 已新增 Todo 领域模型、`ITodoStore` 抽象与 `JsonTodoStore` 文件持久化实现，JSON 作为唯一真相源。
- 已新增 `TodoManager`、`todo_read`、`todo_write`，并完成最大 20 条、唯一 `in_progress`、严格输入校验、统计与 Markdown 视图输出。
- 已在 `AgentOrchestrator` 中接入 Todo Board 初始化、Todo 工具结果写回历史、10 轮未更新提醒和 Todo 专用前端事件。
- 已在前端新增 Agent 阶段的 Todo Board 面板，并完成 `activePlan -> Todo Board` 的执行期初始化链路。
- 已补充相关自动化测试并通过应用层测试；AddIn 真宿主构建仍受本机缺少 VSTO targets 限制，需要在具备 Office/VSTO 环境的机器上继续验证。

## 9. 本轮缺陷修复补充

本轮新增修复一个 Agent 编排器中的状态机分类错误：

- 之前编排器把所有 `RequiredPermission == Write` 的工具都当成“文档写工具”处理。
- `todo_write` 虽然需要写权限，但它写的是 Todo Board 状态，不是 Word 文档内容。
- 该误分类会让 `todo_write` 误入 `UndoScope + PendingWriteStep + AutoVerify` 链路，继而触发“验证失败”“待修复”“任务结束时统一回滚”的连锁问题。

本轮修复目标：

1. 保留 `todo_write` 的 `Write` 权限与 Agent 暴露能力。
2. 仅让真正修改 Word 文档的 `patch_range` / `execute_script` 进入写后验证与修复状态机。
3. 为 `todo_write` 增加回归测试，确保它不会触发文档写入验证失败，也不会导致整次任务的文档写入被误回滚。
