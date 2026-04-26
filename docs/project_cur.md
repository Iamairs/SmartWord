# 当前需求说明：Plan -> Agent 默认按当前计划重建 Todo Board

## 1. 背景

此前 Agent 启动时，如果检测到历史 Todo Board 处于 `RecoveryRequired`、`Paused` 或疑似运行中残留状态，会先触发恢复/暂停决策面板，让用户选择恢复旧板、按当前计划重建或丢弃。

这套策略适合“继续上次任务”的 Agent 入口，但不符合当前 Plan -> Agent 的目标体验。用户在 Plan 面板点击“开始执行”时，已经明确表达“按刚生成的当前计划执行”，此时继续弹恢复选择会打断流程，也可能让旧 Todo Board 污染新计划。

## 2. 当前需求

Plan -> Agent 不再弹 Todo 恢复选择，直接总是按当前 `ActivePlan` 重建 Todo Board。

具体要求：

1. 只要 Agent 请求携带 `ActivePlan`，且不是前端已经显式提交了暂停/恢复决策，就强制用当前计划重建 Todo Board。
2. 历史 Todo Board 即使是 `RecoveryRequired`、`Paused`、`Running` 残留或旧计划，也不阻断 Plan -> Agent。
3. 暂停面板里用户显式选择的 `recover_existing / rebuild_from_active_plan / discard_and_create_empty` 仍继续按用户选择执行，不能被自动重建逻辑覆盖。
4. 更新测试和文档，保证行为稳定。

## 3. 设计决策

- `ActivePlan` 在无显式恢复决策时代表当前用户意图，优先级高于历史 Todo Board。
- 显式恢复/继续决策优先级高于自动策略，因为那是用户在异常/暂停面板上的直接选择。
- 不修改前端 Plan 面板交互；前端已经传入 `activePlan`，本轮修复集中在后端启动准备逻辑。

## 4. 交付范围

- `TodoManager.PrepareBoardForRunAsync` 增加强制按当前计划重建参数。
- `AgentOrchestrator` 在 Plan -> Agent 这类携带 `ActivePlan` 且无显式决策的路径启用强制重建。
- 补充 TodoManager 与 AgentOrchestrator 测试。
- 更新 `docs/已实现的功能.md`。
