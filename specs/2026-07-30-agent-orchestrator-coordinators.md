# AgentOrchestrator 协调器继续拆分 Spec

## 需求背景

`AgentOrchestrator` 已完成首轮物理拆文件，并提取了 Plan 采访、工具准备、LLM 单轮执行、事件工厂和写状态对象。但核心运行循环仍超过 1500 行，审计遥测、Todo 运行生命周期和写步骤验证仍通过 partial 方法直接依赖主编排器字段，尚未达到 OPT-P0-004“主编排器只保留总流程协调”的验收口径。

## 目标

- 提取独立的 `RunAuditRecorder`，统一管理任务历史与 Agent Telemetry 记录。
- 提取独立的 `TodoRunCoordinator`，封装 Todo Board 启动、恢复、暂停和检查点副作用。
- 提取独立的 `WriteStepCoordinator`，封装写步骤的自动验证、状态转换、提交与回滚决策。
- 保持 Ask、Plan、Agent 三种模式的公共接口、事件协议和事件顺序不变。
- 为新组件补充独立单元测试，并继续使用现有编排器特征测试锁定跨组件事件序列。

## 修改范围

- `src/SmartWord.Application/Orchestration` 下的 Agent 编排代码和新增内部协调器。
- `SmartWord.Application.csproj` 中传统编译项登记。
- `tests/SmartWord.Application.Tests/Orchestration` 下的组件测试与事件序列测试。
- `docs/优化问题跟踪.md` 与 `docs/已实现的功能.md` 中的状态说明。

## 不在范围

- 不修改 `IAgentOrchestrator.RunAsync`、`AgentEvent`、工具 Schema 或 WebView RPC 契约。
- 不改变确认、权限、Todo 恢复、Undo、自动验证、历史持久化或 Telemetry 的业务语义。
- 不拆分 Ask、Plan、Agent 为三套重复主循环。
- 不引入新的第三方依赖。
- 不配置安装 Microsoft Office 的 CI Runner。

## 实现方案

1. 先以组件测试锁定审计失败隔离、Telemetry 字段映射、Todo 启动恢复和写步骤状态转换。
2. 将任务历史与 Telemetry 依赖注入 `RunAuditRecorder`，由主循环在原有时点调用，记录失败继续保持只告警、不打断主流程。
3. 将 Todo Board 启动准备、恢复决策、运行开始、暂停和回滚检查点封装为显式结果对象；结果携带主循环需要按顺序发送的领域事件。
4. 将自动验证执行、写步骤成功提交、失败回滚和修复状态转换封装进 `WriteStepCoordinator`；主循环继续负责工具调用事件和 Todo 事件的相对顺序。
5. 删除已迁移的 `AgentOrchestrator` partial 方法和不再需要的字段，保持公共构造函数兼容，由构造函数组装内部组件。
6. 通过传统 csproj 显式登记新增文件，并检查新组件没有反向依赖 AddIn 或 Infrastructure。

## 测试计划

- 运行 `SmartWord.Application.Tests`，覆盖新协调器的成功、失败、取消和无依赖降级路径。
- 运行现有 `AgentOrchestratorPhase3Tests`，重点核对确认、写入成功、验证失败、修复成功、当前步骤回滚、Todo 暂停恢复、取消和上下文压缩事件序列。
- 运行 `./build.ps1 -Core`。
- 运行 `./build.ps1 -AddIn`，验证 VSTO 组合根和传统项目编译项。
- 运行 `./build.ps1 -WordIntegration`，验证真实 Word 写入、验证、取消回滚和进程清理。
- 运行 `git diff --check`。

## 验收标准

- `RunAuditRecorder`、`TodoRunCoordinator` 和 `WriteStepCoordinator` 均为独立 `internal sealed` 组件，并有直接单元测试。
- `AgentOrchestrator` 不再直接持有任务历史、Telemetry 和 Todo 恢复通道的实现细节。
- 写步骤验证和 Undo 决策不再由 `AgentOrchestrator` partial 方法实现。
- 现有公共构造入口保持兼容，相关单元测试、AddIn 构建和真实 Word 集成测试通过。
- 前端可观察的事件类型、内容和顺序保持不变。

## 风险与注意事项

- `IAsyncEnumerable` 无法直接从普通协调器方法透传 `yield`，协调器应返回有序事件集合或结果对象，由主循环在原位置发送。
- Todo Board 状态和写步骤 Undo 生命周期存在耦合，迁移时必须保留“先回滚写步骤，再恢复 Todo 检查点”的顺序。
- 取消发生在工具执行期间时，必须等待工具清理完成并回滚当前步骤，之后才发送单一 `Cancelled` 事件。
- 审计和 Telemetry 属于旁路能力，任何记录异常都不得覆盖主任务结果。
- 新组件不能重新形成超大类；纯事件构造和纯状态转换优先复用现有工厂与状态对象。
