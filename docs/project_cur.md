# 当前需求说明：Todo Board 生命周期与异常恢复优化

## 1. 背景

当前 SmartWord 已经实现 Todo Board 的基础闭环：

- Agent 阶段可初始化、读取、更新 Todo Board
- `todo_read` / `todo_write` 已接入模型工具链
- 前端可展示执行期任务板
- Reminder 与写后验证链路已具备首版能力

但现有实现仍把 Todo JSON 当成“持久化缓存”，没有把它当作“带运行状态的执行状态对象”来管理，导致两个问题：

1. 任务成功后旧任务板仍残留在磁盘，下次执行容易被误当成当前任务继续使用。
2. 任务取消、异常中断、写步骤回滚、宿主崩溃后，没有显式恢复入口，系统可能继续默默沿用一块可疑的旧板。

## 2. 当前需求

本轮需要把 Todo Board 升级为“可恢复的执行状态”，核心目标如下：

1. 成功完成任务后自动删除 Todo 文件，避免历史残留污染下一次运行。
2. 任务取消、异常、回滚、崩溃等异常路径保留 Todo 文件，但显式标记为待恢复。
3. 下次进入 Agent 前必须先完成恢复决策，模型不能直接消费脏任务板。
4. 前端提供恢复决策入口，由用户决定“恢复旧板 / 按 ActivePlan 重建 / 丢弃并新建空板”。
5. JSON 持久化增强为临时文件写入后原子替换，并对损坏 JSON 提供受控恢复入口。

## 3. 已确认的关键决策

### 3.1 生命周期策略

- Agent 一启动就把 Todo Board 标记为 `Running`
- 正常完成后调用统一删除逻辑
- 已知异常路径统一标记为 `RecoveryRequired`
- 如果进程崩溃导致未收尾，下次看到 `Running` 视为“疑似崩溃”并要求恢复决策

### 3.2 恢复策略

- 默认不做静默自动修复
- 默认先提示用户选择恢复策略
- 不做 Word 文档内容级自动比对
- 不追求 Todo JSON 与 Word Undo 的强事务一致性

### 3.3 ActivePlan 的角色

- `ActivePlan` 仅作为“可选重建源”
- 不再无条件覆盖已有异常 Todo Board
- 只有用户选择“按当前计划重建”时才重新生成任务板

## 4. 期望包含的核心能力

### 4.1 Todo Board 运行元数据

需要在 `TodoBoard` 上新增：

- `ExecutionState`
- `LastRunId`
- `LastRunStartedAtUtc`
- `LastRunFinishedAtUtc`
- `LastRunOutcome`
- `LastErrorSummary`
- `RecoveryReason`
- `SourcePlanFingerprint`

### 4.2 TodoManager 生命周期 API

需要由 `TodoManager` 统一提供：

- `PrepareBoardForRunAsync`
- `MarkRunStartedAsync`
- `MarkRunSucceededAndDeleteAsync`
- `MarkRunInterruptedAsync`
- `ResolveRecoveryAsync`
- `DiscardBoardAsync`

### 4.3 编排器恢复握手

在模型调用前加入恢复判断：

- 若发现 `RecoveryRequired` 或上次停留在 `Running`
- 先发出 `todo_board_recovery_required`
- 阻塞主循环，等待前端回传恢复决策
- 决策完成后再发 `todo_board_ready`

### 4.4 前端恢复入口

前端新增独立恢复面板，而不是复用普通 Todo 面板：

- 展示恢复原因、最近结果、错误摘要
- 提供三个动作按钮
- 将用户决策回传 C#

### 4.5 JSON 可靠性

- `JsonTodoStore.SaveBoardAsync` 需要改成临时文件写入后原子替换
- 反序列化失败时要进入受控恢复路径
- 保留按文档路径 hash 分桶的现有目录策略

## 5. 设计约束与注意事项

### 5.1 技术约束

- 后端仍为 `.NET Framework 4.7.2`
- 文档与代码文件均使用 UTF-8
- 新增代码注释需使用中文

### 5.2 架构约束

- Todo 生命周期规则集中在 Application 层的 `TodoManager`
- 前端只消费后端给出的权威状态和恢复说明，不自行推断
- AddIn 只做桥接与等待，不承载 Todo 业务判断

### 5.3 交付边界

- 本轮不做 Word 文档级自动比对
- 本轮不做 SQLite 迁移
- 本轮继续沿用 `%AppData%\\SmartWord\\todo\\<hash>.json`

## 6. 本轮交付目标

1. 完成 Todo Board 执行状态模型升级。
2. 完成 TodoManager 生命周期和恢复 API。
3. 完成 Agent 编排器恢复握手与统一收尾。
4. 完成 WebView 桥接和前端恢复决策面板。
5. 完成关键测试并更新 `docs/已实现的功能.md`。
