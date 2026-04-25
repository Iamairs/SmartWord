# Todo List

- [x] P1 统一后端固定轮次预算为 100，并修正上限命中的停止语义
- [x] P2 为 Todo Board 增加 `Paused / PausedByBudget` 暂停态与生命周期处理
- [x] P3 改造前端暂停提示与继续执行入口，保留现有恢复链路
- [x] P4 补充测试、重新构建前端资源并更新实现文档

> 说明：本文件用于跟踪本轮“固定 100 轮预算与上限命中收尾优化”的执行进度。

# 实施计划：固定 100 轮预算与上限命中收尾优化

## 1. 目标

在不改成无限循环的前提下，把 Ask / Plan / Agent 的预算控制统一为固定 100 轮，并确保：

- 未完成任务不再被误判为成功完成
- Agent 命中预算上限后进入可信暂停态，而不是删板或进入异常恢复态
- 前端能明确区分“异常恢复”和“预算暂停”

## 2. 分阶段实施步骤

## 阶段 P1：修正后端预算与收尾语义

- 将 `AgentRunOptions.MaxIterations` 默认值改为 100
- 将 `AgentOrchestrator.ResolveMaxIterations` 改为固定上限 100
- Ask / Plan / Agent 命中预算后统一发出 `MaxIterationsReached`
- Ask / Plan 命中上限后直接停止本轮，不再发 `TaskCompleted`
- 修正 `AgentOrchestrator` 中“自然跑满上限仍被当作成功”的错误路径

## 阶段 P2：扩展 Todo 暂停态

- `TodoBoardExecutionState` 新增 `Paused`
- `TodoBoardRunOutcome` 新增 `PausedByBudget`
- `TodoBoardPreparationStatus` 新增 `Paused`
- `TodoManager` 新增 `MarkRunPausedAsync`
- 启动前若发现 `Paused`，允许继续旧板 / 按计划重建 / 丢弃并新建空板

## 阶段 P3：前端暂停交互

- 新增 `TodoBoardPausePanel.vue`
- `chatStore` 新增 `pendingTodoPause` 与 `lastApprovedPlan`
- `ChatWindow.vue` 支持：
  - 处理 `max_iterations_reached`
  - 处理 `todo_board_paused`
  - 从暂停面板继续执行，并把显式决策带回后端
- 保留现有 `TodoBoardRecoveryPanel`，继续只处理异常恢复

## 阶段 P4：验证与文档

- 补充 `TodoManagerTests`
  - `MarkRunPausedAsync` 的持久化与再次准备行为
- 补充 `AgentOrchestratorPhase3Tests`
  - Ask 命中上限不再完成
  - Plan 命中上限不再完成
  - Agent 命中上限进入 `Paused`
  - 前端传入大于 100 的预算时仍被限制为 100
- 执行：
  - `dotnet test tests/SmartWord.Application.Tests/SmartWord.Application.Tests.csproj`
  - `npm run build`
- 更新 `docs/已实现的功能.md`

## 3. 当前状态

- [x] P1 已完成：默认预算与强制上限已统一为 100，并修复了上限命中后继续走成功收尾的问题。
- [x] P2 已完成：Todo Board 已支持 `Paused / PausedByBudget`，Agent 达到预算上限后可保留可信暂停态。
- [x] P3 已完成：前端已新增暂停面板，并支持继续执行 / 按计划重建 / 丢弃后新建空板。
- [x] P4 已完成：应用层测试通过，前端已重新构建，文档已同步更新。

## 4. 风险与注意事项

- 暂停态继续执行依赖当前对话历史和 Todo Board 状态，仍建议用户在长任务中及时确认阶段性结果
- 本轮仍未引入 token/time/tool 多维预算，只针对固定轮次预算做收尾修正
- AddIn 真宿主工程仍受本机 VSTO 依赖限制，需在具备 Office 环境的机器上做最终宿主验证
