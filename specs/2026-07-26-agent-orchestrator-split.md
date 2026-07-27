# AgentOrchestrator 职责拆分 Spec

## 需求背景

`AgentOrchestrator` 当前接近 4000 行，同时承担主循环、Plan 采访、工具执行、写后验证、Todo 生命周期、审计遥测、事件构造和工具结果装饰等职责。代码已难以独立理解和测试，继续扩展会增加回归风险。

## 目标

- 将 `AgentOrchestrator` 拆分为职责清晰的内部组件。
- 将主编排器控制在约 800 至 1200 行，仅保留顶层流程与事件顺序协调。
- 保持 Ask、Plan、Agent 三种模式的现有行为和公共契约不变。
- 为抽出的纯逻辑补充独立单元测试。

## 修改范围

- `SmartWord.Application/Orchestration` 下的编排器及新增内部组件。
- `SmartWord.Application.csproj` 的传统编译项登记。
- `SmartWord.Application.Tests` 中与新组件及事件序列相关的测试。
- 必要时添加测试程序集访问内部类型的配置。

## 不在范围

- 不修改 `IAgentOrchestrator.RunAsync` 签名。
- 不修改 `AgentEvent`、前端 RPC 或工具 Schema。
- 不拆成三套独立且重复的 Ask、Plan、Agent 主循环。
- 不修改或提交当前工作区中的 BenchmarkScorer 相关改动。
- 不主动改变权限、撤销、验证、Todo 或持久化语义。

## 实现方案

1. 引入单次运行状态对象，集中管理消息、Todo Board、写步骤、失败计数、审计进度和完成状态。
2. 抽取 Plan 采访、工具调用、写操作、Todo 生命周期、审计遥测、事件工厂及工具结果装饰组件。
3. 保留现有公共构造函数，由其组装内部组件，避免影响 AddIn、EvalRunner 和现有测试。
4. 用户确认事件仍由主循环先发送，再等待确认通道，避免前端等待死锁。
5. 写操作继续使用单步骤 Undo：验证成功提交，执行或验证失败只回滚当前步骤。
6. 工具结果继续保持 OpenAI assistant/tool 消息严格配对。
7. 新组件使用 `internal sealed`，不扩大程序集公共 API。

## 测试计划

- 运行现有 `AgentOrchestratorPhase3Tests` 全部测试。
- 补充 Plan 采访、事件工厂、工具结果装饰、自动验证计划和写步骤状态转换测试。
- 覆盖确认、写入成功、验证失败、修复成功、Todo 恢复、上下文压缩和迭代超限的事件序列。
- 构建 `SmartWord.Application` 和 `SmartWord.EvalRunner`；环境允许时构建完整解决方案。

## 验收标准

- 所有相关测试通过。
- AddIn、EvalRunner 和测试项目中的三个构造入口保持可编译。
- 主编排器约 800 至 1200 行，且新组件没有重新形成超大类。
- 公共接口、事件协议及关键行为保持兼容。

## 风险与注意事项

- `IAsyncEnumerable` 的事件发送顺序属于行为契约，拆分时必须逐场景回归。
- 用户确认必须发生在确认事件发送之后。
- 写后验证和 Undo 生命周期耦合紧密，应作为整体迁移，避免跨组件重复管理资源。
- 传统 `csproj` 不会自动包含新增文件，必须显式登记编译项。
- 若发现确定性缺陷，只在具备回归测试时修复，并使用独立 `fix` 提交。
