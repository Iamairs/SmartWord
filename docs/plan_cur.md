# Todo List

- [x] P1 升级 TodoBoard 模型与恢复枚举，补齐运行状态字段
- [x] P2 重构 TodoManager 与 JsonTodoStore，完成生命周期 API 与原子写入
- [x] P3 改造 AgentOrchestrator / WebViewBridge，加入恢复握手与统一收尾
- [x] P4 改造前端 store / bridge / UI，新增恢复决策面板
- [x] P5 补齐测试、执行验证并更新实现文档

> 说明：本文件用于跟踪本轮“Todo Board 生命周期与异常恢复优化”的执行进度；每完成一个阶段，需要同步更新状态与验证结果。

# 实施计划：Todo Board 生命周期与异常恢复优化

## 1. 目标

将 Todo Board 从“纯持久化缓存”升级为“带运行状态的可恢复执行状态”，确保：

- 成功后自动删板
- 异常后显式进入恢复态
- 下次运行必须先做恢复决策
- 模型只消费已确认可用的任务板

## 2. 分阶段实施步骤

## 阶段 P1：升级 TodoBoard 模型

- 在 `TodoBoard` 中新增执行状态、最近运行结果、恢复原因、计划指纹等元数据
- 新增恢复相关枚举与结果模型
- 保证旧 JSON 缺字段时仍能按默认值读取

## 阶段 P2：重构 TodoManager 与 JsonTodoStore

- 将 `TodoManager` 从“确保有板 + 普通写入”扩展为完整生命周期管理器
- 新增：
  - `PrepareBoardForRunAsync`
  - `MarkRunStartedAsync`
  - `MarkRunSucceededAndDeleteAsync`
  - `MarkRunInterruptedAsync`
  - `ResolveRecoveryAsync`
  - `DiscardBoardAsync`
- `JsonTodoStore` 改为临时文件写入后原子替换
- 对 JSON 反序列化失败返回受控异常，供恢复链路消费

## 阶段 P3：改造 AgentOrchestrator 与 WebViewBridge

- Agent 进入模型前先执行恢复判断
- 新增 `TodoBoardRecoveryRequired` 事件
- 编排器等待前端恢复决策后再继续
- 正常完成走删板逻辑
- 取消、异常、待修复终止、写后回滚统一标记为 `RecoveryRequired`
- `WebViewBridge` 增加恢复决策提交接口和事件映射

## 阶段 P4：改造前端恢复入口

- `chatStore` 新增恢复态数据
- `hostBridge` 增加恢复决策调用
- 新增恢复面板组件，展示：
  - 恢复原因
  - 最近运行结果
  - 最近错误摘要
  - 三个恢复动作
- `TodoBoardPanel` 继续只负责稳定任务板展示

## 阶段 P5：测试与文档

- 补充 `TodoManagerTests`
  - 成功后删板
  - 取消/异常后保留并标记恢复
  - 三个恢复决策分支
  - JSON 损坏处理
- 补充 `AgentOrchestratorPhase3Tests`
  - 发现脏板先发恢复事件并阻塞模型
  - 恢复完成前不向模型注入旧 Todo
  - 成功完成时删板
  - 异常路径标记恢复态
- 执行：
  - `dotnet test tests/SmartWord.Application.Tests/SmartWord.Application.Tests.csproj`
  - `npm run build`
- 更新 `docs/已实现的功能.md`

## 3. 当前状态

- [x] P1 已完成：已新增执行状态、运行结果、恢复决策枚举与准备结果模型，并保持旧 JSON 缺字段可按默认值读取。
- [x] P2 已完成：`TodoManager` 已具备准备/启动/成功删板/异常标记/恢复决策等生命周期 API，`JsonTodoStore` 已改为临时文件写入后原子替换。
- [x] P3 已完成：`AgentOrchestrator` 已在模型前加入恢复握手，`WebViewBridge` 已支持恢复事件映射与决策回传。
- [x] P4 已完成：前端已新增恢复态 store、桥接调用与 `TodoBoardRecoveryPanel`，稳定任务板与恢复控制流已分离。
- [x] P5 已完成：`dotnet test tests/SmartWord.Application.Tests/SmartWord.Application.Tests.csproj` 与 `npm run build` 已通过，相关实现文档已更新。

## 4. 风险与注意事项

- 需要保持旧版 Todo JSON 的向后兼容
- 编排器已有写步骤验证状态机，恢复收尾需要避免与其冲突
- 前端浏览器降级模式也要给出最小可运行的恢复交互
