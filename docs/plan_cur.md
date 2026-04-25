# Todo List

- [x] P0 写入本轮需求文档 `project_cur.md` 与 `plan_cur.md`
- [x] P1 改造 Undo 接口与 Word 集成层为步骤级 Undo
- [x] P2 重构 AgentOrchestrator 为“步骤级提交 + 当前步回滚 + 暂停决策”
- [x] P3 扩展 TodoManager / TodoBoard，接入当前写步骤快照与可信检查点
- [x] P4 收紧 Todo 写入约束并改造前端暂停/恢复语义
- [x] P5 补齐测试、跑验证、更新文档并准备提交 git

> 说明：本文件用于跟踪本轮“Agent 回滚策略重构为步骤级提交 + 当前步回滚 + 暂停决策”的执行进度；每完成一个阶段，都要同步更新状态。

# 实施计划：Agent 回滚策略重构

## 1. 目标

把当前 Agent 模式的回滚策略从“任务级回滚”改成“步骤级提交 + 当前步回滚 + 暂停决策”，确保：

- 已验证成功的文档改动可以保留
- 当前失败步骤可以单独撤回
- Todo Board 与当前步回滚同步恢复
- 失败 3 次后暂停并交给用户决策，而不是把整轮成果全部抹掉

## 2. 分阶段实施步骤

### 阶段 P1：改造 Undo 接口与 Office 集成层

- 把 `IUndoScopeFactory.BeginTaskUndoAsync` 改成步骤级语义接口
- 调整 `WordApplicationWrapper`，按写步骤创建新的 Undo 记录
- 调整 `UndoRecordWrapper` 的注释、日志和行为语义，明确它只负责当前步骤

### 阶段 P2：重构 AgentOrchestrator 主循环

- 删除“整次任务共享一个 UndoScope”的旧逻辑
- 引入当前写步骤事务对象，包含：
  - 当前工具调用信息
  - 当前步影响段落
  - 当前步重试次数
  - 当前步 Todo 快照
  - 当前步 UndoScope
- 写步骤执行成功但未验证前，不允许进入下一个独立写步骤
- 当前步骤执行失败或验证失败时，只回滚当前步骤
- 当前步骤连续失败达到上限后，进入暂停态，而不是整轮回滚

### 阶段 P3：扩展 TodoManager 与 TodoBoard

- 在 `TodoBoard` 上增加：
  - 当前进行中写步骤标识/摘要
  - 当前写步骤起点的 Todo 快照
  - 最近可信检查点信息
  - 暂停原因
- 在 `TodoManager` 中新增：
  - `MarkWriteStepStartedAsync`
  - `MarkWriteStepCommittedAsync`
  - `RollbackCurrentWriteStepAsync`
- “恢复旧任务板”改成恢复到最近可信快照，而不是沿用失败前漂移板

### 阶段 P4：收紧 Todo 写入与前端交互

- 当前写步骤未提交前，禁止推进当前活动任务的 `todo_write`
- 前端暂停面板文案要明确：
  - 当前步骤已回退
  - 之前成功步骤已保留
- 恢复面板文案要明确：
  - 状态不可信时建议重建

### 阶段 P5：测试、文档、提交

- 补充应用层测试：
  - 第一步成功、第二步失败时，第一步保留、第二步回滚
  - 当前步失败 3 次后进入暂停
  - 当前步回滚时 Todo 一起恢复
  - 当前写步骤未提交时推进当前 Todo 会被拒绝
- 运行测试与必要构建
- 更新 `docs/已实现的功能.md`
- 按原子化要求提交 git

## 3. 当前状态

- [x] P0 已完成：需求文档与执行计划文档已建立。
- [x] P1 已完成：Undo 工厂与 Word UndoRecord 已切到步骤级语义。
- [x] P2 已完成：AgentOrchestrator 已改为每个写步骤独立开启 Undo，并在失败时只回退当前步骤、进入暂停决策。
- [x] P3 已完成：TodoBoard / TodoManager 已支持可信检查点、当前写步骤快照、恢复旧板恢复到可信快照。
- [x] P4 已完成：Todo 写入已接入写步骤锁定保护，前端暂停面板已改为表达“当前步已回退、旧成果保留”。
- [x] P5 已完成：应用层测试通过，前端构建通过，`docs/已实现的功能.md` 已更新，当前进入 git 提交收尾。

## 4. 风险与注意事项

- 这轮改造会触及编排器主循环、Todo 生命周期与 Word Undo 集成，属于高耦合重构
- 必须避免把“步骤级回滚”和“预算暂停”混成同一个状态
- 必须确保旧版 Todo JSON 仍能读取，并给出合理默认值
- 受当前机器环境限制，`SmartWord.AddIn` 仍无法完成最终宿主工程编译，需要在具备 Office VSTO targets 的机器补一次整仓验证
