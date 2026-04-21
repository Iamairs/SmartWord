# Todo List

- [x] P1 建立 Todo Board 领域模型、JSON 存储抽象与基础序列化协议
- [x] P2 实现 TodoManager、TodoRead / TodoWrite 工具及严格输入校验
- [x] P3 将 Todo 闭环与 reminder 机制接入 Agent 编排主循环
- [x] P4 实现前端 Agent 阶段 Todo Board 面板、事件接收与状态管理
- [x] P5 补齐自动化测试、验证构建并更新实现文档

> 说明：以上 Todo List 将作为实施过程中的动态状态面板持续维护；每完成一个阶段，需要同步更新本文件中的状态、记录产出与遗留问题。

## 当前状态

- 已完成 Todo Board 全链路实现，包含 Core 模型、JSON 文件持久化、TodoManager、Todo 工具、Agent 主循环挂钩、前端任务板面板和自动化测试。
- 已完成 Plan -> Agent 过渡时基于 `activePlan` 初始化 Todo Board，并在 Agent 阶段通过 `todo_board_ready` / `todo_board_updated` / `todo_reminder_injected` 事件实时同步前端。
- 当前遗留仅包括后续增强项：SQLite 持久化、多端同步、真实 VSTO 宿主 E2E 验证；其中 AddIn 工程在本机因缺少 `Microsoft.VisualStudio.Tools.Office.targets` 无法完成宿主编译验证。

# 实施计划：Todo Board 任务板体系

## 1. 目标

在 SmartWord 中实现一套完整的 Todo Board 机制，用于支持复杂任务的结构化规划、执行中状态维护、模型自监督反馈与前端实时可视化。首版采用 JSON 文件作为权威存储，不接入数据库，但为后续 SQLite 留出抽象扩展点。

## 2. 范围与交付边界

### 2.1 本轮纳入范围

- Todo Board 核心领域模型
- Todo 文件存储抽象与 JSON 实现
- TodoManager 业务规则中心
- `TodoRead` / `TodoWrite` 工具
- Agent 编排器中的 Todo 反馈闭环
- 连续 10 轮未更新 Todo 的提醒机制
- Agent 阶段前端 Todo Board 面板
- 关键单元测试 / 集成级流程测试

### 2.2 本轮不纳入范围

- SQLite 持久化
- 多文档并发协作
- 跨机器同步
- Plan 阶段前端直接改造为 Todo Board

## 3. 总体设计

### 3.1 权威数据源

- 以 `TodoBoard` 结构作为唯一真相源
- 通过 `ITodoStore` 抽象读写能力
- 首版提供 `JsonTodoStore`
- Markdown 仅由 `TodoManager` 按当前任务板实时生成，用于工具输出和调试

### 3.2 模型交互方式

模型不直接编辑 Markdown，也不直接覆盖整个文件内容。统一通过结构化工具调用：

- `TodoRead`：读取当前任务板
- `TodoWrite`：发起受限动作，更新任务板

编排器执行工具后，将最新任务板摘要作为工具结果写回对话历史，使模型下一轮能看到更新后的任务板。

### 3.3 前端同步方式

后端在 Todo Board 初始化或更新后，向前端发出 Todo 专用事件，并携带完整任务板 JSON 快照。前端不做差量推断，只做整板替换与渲染。

## 4. 分阶段实施步骤

## 阶段 P1：建立 Todo Board 领域模型、JSON 存储抽象与基础序列化协议

### 4.1 Core 层模型

新增或补充以下核心模型：

- `TodoBoard`
- `TodoBoardItem`
- `TodoBoardStats`
- `TodoBoardStatus` / `TodoItemStatus`
- `TodoWriteRequest`
- `TodoWriteResult`

建议字段：

- `TodoBoard`
  - `BoardId`
  - `DocumentPath`
  - `Version`
  - `UpdatedAt`
  - `RoundsSinceLastTodoUpdate`
  - `LastReminderRound`
  - `Items`
- `TodoBoardItem`
  - `Id`
  - `Content`
  - `Status`
  - `Order`
  - `Notes`
  - `CreatedAt`
  - `UpdatedAt`
  - `CompletedAt`

### 4.2 存储抽象

新增接口：

- `ITodoStore`

接口建议能力：

- `GetBoardAsync(documentPath, cancellationToken)`
- `SaveBoardAsync(board, cancellationToken)`
- `DeleteBoardAsync(documentPath, cancellationToken)`
- `ExistsAsync(documentPath, cancellationToken)`

### 4.3 JSON 实现

新增 `JsonTodoStore`：

- 存储目录建议使用 `%AppData%\\SmartWord\\todo\\`
- 文件名由文档路径做稳定 hash 生成
- 文件内容采用结构化 JSON，并携带 `schema_version`
- 对损坏文件返回受控错误，不允许默默吞掉异常

### 4.4 辅助能力

可新增以下辅助类：

- `TodoPathResolver`
- `TodoBoardJsonSerializer`

目的：

- 隔离路径规则
- 隔离 JSON 兼容性与序列化细节

## 阶段 P2：实现 TodoManager、TodoRead / TodoWrite 工具及严格输入校验

### 4.5 TodoManager

新增 `TodoManager` 作为唯一规则中心，职责：

- 加载 / 创建任务板
- 保存任务板
- 执行任务板变更
- 计算统计信息
- 生成 Markdown 视图
- 严格校验输入

### 4.6 TodoWrite 动作设计

`TodoWrite` 采用命令式动作，不允许模型自由提交原始文件内容。建议支持：

- `reset_board`
- `add_item`
- `update_item`
- `set_status`
- `remove_item`
- `reorder_items`
- `replace_board`

其中：

- `replace_board` 主要用于 Plan 结束后首次把计划落成任务板
- 普通 Agent 执行阶段主要用 `add_item` / `set_status` / `update_item`

### 4.7 严格校验规则

必须校验：

- `id` 非空
- `content` 去空白后非空
- `status` 合法
- `order` 合法
- 不允许重复 id
- 不允许超过 20 条
- 不允许同时存在多条 `in_progress`
- 更新或删除时目标 id 必须存在
- `replace_board` 时整板先校验后提交，失败则整板拒绝

### 4.8 工具实现

新增：

- `TodoReadTool`
- `TodoWriteTool`

工具输出至少包含：

- `success`
- `operation`
- `board_id`
- `board_version`
- `current_active_id`
- `stats`
- `items`
- `markdown_view`

### 4.9 工具注册与模式暴露

在工具注册中心中：

- `TodoReadTool` 对 Plan / Agent 暴露
- `TodoWriteTool` 对 Agent 暴露

如需在 Plan 模式下允许模型先创建任务板，可额外评估是否将 `replace_board` 作为编排器内部转换，而不是直接向 Plan 模式暴露写工具。

## 阶段 P3：将 Todo 闭环与 reminder 机制接入 Agent 编排主循环

### 4.10 编排器接入点

在 `AgentOrchestrator` 中补齐以下能力：

- 跟踪本轮是否触发 Todo 工具
- 记录连续未更新 Todo 的轮次
- 成功执行 `TodoWrite` 后重置计数器
- 达到阈值后插入 system reminder

### 4.11 Reminder 机制

新增 `TodoReminderService` 或在编排器中局部封装提醒策略：

- 连续 10 轮未触发 Todo 时插入一条温和 reminder
- reminder 只在 Plan / Agent 模式下启用
- 同一阈值段内避免重复轰炸
- `TodoRead` 是否计入“已感知任务板”需要单独约定：
  - 推荐：`TodoRead` 视为感知，但不视为真正更新
  - `TodoWrite` 成功则重置全部 reminder 计数

### 4.12 对话历史闭环

保持现有 `AppendToolResultAsync` 机制不变，但确保：

- `TodoWrite` 输出中包含完整任务板摘要
- 模型下一轮能直接读到最新任务板
- 失败时返回清晰错误，促使模型修正输入

### 4.13 Plan → Agent 过渡

在用户从 Plan 面板点击“开始执行”时：

- 将 `ExecutionPlan` 转成标准 `TodoBoard`
- 初始化 JSON 存储
- 再进入 Agent 模式

避免继续仅用自然语言拼接计划文本作为唯一执行上下文。

### 4.14 Todo 专用事件

新增 `AgentEventType`：

- `TodoBoardReady`
- `TodoBoardUpdated`
- `TodoReminderInjected`

事件负载至少包含：

- `BoardJson`
- `Message`
- `CompletedSteps`
- `TotalSteps`
- `CurrentTodoId`

## 阶段 P4：实现前端 Agent 阶段 Todo Board 面板、事件接收与状态管理

### 4.15 Store 扩展

在前端 `chatStore` 中新增：

- `activeTodoBoard`
- `todoBoardVisible`
- `todoBoardStats`
- `todoBoardLastUpdatedAt`

### 4.16 组件实现

新增 `TodoBoardPanel.vue`：

- 显示任务列表
- 显示状态徽标
- 高亮当前 `in_progress`
- 显示统计数据
- 显示最后更新时间

### 4.17 页面接入

在 `ChatWindow.vue` 中：

- 保留现有 Plan 面板
- 进入 Agent 执行后显示 Todo Board 面板
- 接收到 `todo_board_ready` / `todo_board_updated` 事件时刷新面板
- 接收到 `todo_reminder_injected` 时可选择仅记录到消息流，不强制额外弹窗

### 4.18 UI 行为约束

- 前端不做局部 patch 推断
- 以后端完整快照覆盖为准
- Agent 执行结束后保留最终任务板状态，便于复盘

## 阶段 P5：补齐自动化测试、验证构建并更新实现文档

### 4.19 单元测试

至少覆盖：

- `TodoManager` 规则校验
- `JsonTodoStore` 文件读写
- `TodoReadTool` / `TodoWriteTool` schema 与结果
- reminder 计数与注入逻辑

### 4.20 编排器测试

至少覆盖：

- `TodoWrite` 成功后写回对话历史
- 连续 10 轮未更新 Todo 时插入 reminder
- reminder 注入后不会每轮重复触发
- `TodoWrite` 成功后计数器清零
- Plan → Agent 初始化 Todo Board
- Todo 事件能被正确发出

### 4.21 前端与构建验证

至少验证：

- 事件驱动下任务板可正确展示
- Agent 执行阶段任务板可实时刷新
- 任务完成后面板保留最终状态
- 前后端工程可成功构建

### 4.22 文档收尾

功能完成并测试通过后：

- 更新 `docs/已实现的功能.md`
- 将本文件中的 Todo List 全部更新为完成状态
- 将阶段性遗留与后续可扩展项记录到文档末尾

## 5. 实施顺序建议

推荐严格按以下顺序推进：

1. Core 模型与存储抽象
2. JSON 存储实现
3. TodoManager 规则与工具
4. 编排器接入与 reminder
5. 前端事件与面板
6. 自动化测试与文档收尾

## 6. 当前默认约定

- 权威存储：JSON
- Markdown：只读展示视图
- 前端显示范围：仅 Agent 阶段
- 数据隔离维度：按文档路径
- 首版不接 SQLite
- Todo Board 为执行期任务板，不替代现有对话历史存储

## 7. 实施结果

### 7.1 P1 结果

- 已新增 `TodoBoard`、`TodoBoardItem`、`TodoBoardStats`、`TodoWriteRequest`、`TodoWriteResult`、`TodoToolMetadata` 等模型。
- 已新增 `ITodoStore` 抽象，并提供 `JsonTodoStore` 作为首版文件持久化实现。
- 已将 Todo 相关新增文件加入传统 csproj 的 `<Compile Include>`，确保旧式工程可正常编译。

### 7.2 P2 结果

- 已实现 `TodoManager`，统一维护最大 20 条、唯一 `in_progress`、非法输入拦截、自动推进下一待办、Markdown 视图和统计数据构建。
- 已新增 `todo_read` / `todo_write` 工具，并通过结构化 schema 限制模型只能按受控动作维护任务板。
- 已通过 `AsyncLocal` 方式在编排器与工具之间传递当前文档路径，保证 Todo 工具在 `ITool.ExecuteAsync` 不带文档参数的前提下仍可按文档隔离读写。

### 7.3 P3 结果

- 已在 `AgentOrchestrator` 中接入 Todo Board 初始化、工具结果回填、Todo 事件透出和 reminder 机制。
- 已将 reminder 策略升级为“仅 `todo_write` 视为真正维护任务板”：探索阶段连续 5 个有效执行轮次未 `todo_write`、发生真实文档写入后连续 3 个有效执行轮次未 `todo_write` 时按 `5/3/5` 节奏注入提醒，并补充“写后未更新”的高优先级提醒。
- 已在系统提示词中追加 Todo Board 区块，使模型能持续感知当前任务板状态。

### 7.4 P4 结果

- 已在前端新增 `TodoBoardPanel.vue`，并扩展 `chatStore` 管理当前 Todo 快照、当前活动任务和任务板可见性。
- 已在 `ChatWindow.vue` 中按 Agent 阶段接入任务板面板，并对 Todo 相关事件执行整板覆盖刷新。
- 已在 Plan 面板点击“开始执行”时把 `activePlan` 一并传给后端，为 Plan -> Agent 初始化任务板提供结构化输入。

### 7.5 P5 结果

- 已新增 `TodoManagerTests`，并补充 `AgentOrchestratorPhase3Tests` 中的 Todo 初始化与 reminder 用例。
- 已验证 `dotnet test tests/SmartWord.Application.Tests/SmartWord.Application.Tests.csproj` 通过。
- 已验证 `npm run build` 通过，并生成新的 WebClient 静态资源。
- `SmartWord.AddIn` 宿主工程编译在当前机器上仍受本地 VSTO 目标文件缺失限制，属于环境阻塞而非本轮代码设计边界。

## 8. 缺陷修复计划：todo_write 误入文档写入验证链

- [x] F1 复核 `AgentOrchestrator` 中写工具分类、自动验证与任务收尾逻辑，确认 `todo_write` 被误当作文档写工具。
- [x] F2 调整编排器分支：仅 `patch_range` / `execute_script` 进入 `UndoScope + PendingWriteStep + AutoVerify` 生命周期。
- [x] F3 保留 `todo_write` 的写权限与 Todo 事件流，但不再要求写前确认、不再触发写后验证、不再阻塞任务提交。
- [x] F4 补充回归测试，覆盖 `todo_write` 不触发 `ChangeVerificationFailed`、不创建 `UndoScope`、任务可正常 `TaskCompleted`。
- [x] F5 重新执行应用层测试，确认真实文档写工具的验证链未被破坏。
