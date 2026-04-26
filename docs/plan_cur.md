# Todo List

- [x] P0 明确 Plan -> Agent 启动策略：当前计划优先，默认重建 Todo Board
- [x] P1 修改 TodoManager，支持强制按 ActivePlan 重建
- [x] P2 修改 AgentOrchestrator，Plan -> Agent 不再弹恢复/暂停选择
- [x] P3 补充 TodoManager 与 AgentOrchestrator 测试
- [x] P4 运行验证、更新文档并提交 git

# 实施计划：Plan -> Agent 默认按当前计划重建

## 1. 目标

让用户在 Plan 面板点击“开始执行”后，Agent 直接以当前计划为准初始化 Todo Board，不再因为历史 Todo Board 的异常、暂停或残留状态弹出恢复选择。

## 2. 实施步骤

1. 在 `TodoManager.PrepareBoardForRunAsync` 增加 `forceRebuildFromActivePlan` 参数。
2. 当该参数为 `true` 且存在 `ActivePlan` 时，直接使用当前计划创建新的 Todo Board 并保存。
3. 在 `AgentOrchestrator` 中判断：`ActivePlan != null` 且 `StartupTodoBoardDecision == null` 时启用强制重建。
4. 保留暂停/恢复面板显式决策的优先级，避免用户点“继续旧任务板”时被自动重建覆盖。
5. 补充测试：
   - 旧板处于恢复态时，强制重建仍返回 Ready。
   - Plan -> Agent 携带 ActivePlan 时不发恢复事件、不等待恢复通道。

## 3. 验证

- `dotnet test tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj --no-restore`

## 4. 注意事项

- 本轮不改普通 Agent 入口的恢复面板逻辑。
- 本轮不改暂停面板三个按钮的语义。
- 当前工作区存在用户未提交的 OfficeIntegration 修改，本轮提交会避开这些文件。
