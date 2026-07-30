# OPT-P0-002 真实 Word 集成测试缺口收口

## 需求背景

`SmartWord.OfficeIntegration.Tests` 已建立真实 Word COM 测试基线，并覆盖文本与格式写入、表格、页眉页脚、验证失败回滚、受保护文档、文档切换和进程清理。但现有取消测试会吞掉 `OperationCanceledException`，未断言编排器产生 `AgentEventType.Cancelled`，且只覆盖写入确认前取消，不能证明已经发生的写步骤会在取消后回滚。现有测试也没有使用只读方式打开真实文档并验证写入拦截。

`docs/instructions/Agent_核心引擎规格.md` 规定取消应由编排器回滚并产生 `Cancelled` 事件；`docs/instructions/Office集成与工具系统.md` 规定 Agent 在文档不可写时应提前停止，UndoRecord 由编排层统一管理。因此需要补齐实现与真实宿主测试，使 OPT-P0-002 的本地基线达到可关闭状态。

## 目标

- 统一用户主动取消的编排器契约：回滚未提交的当前写步骤，记录取消状态，并向调用方产生且只产生一条 `AgentEventType.Cancelled` 事件。
- 补充真实 Word 只读文档写入拦截测试，确认不调用写工具且文档不变。
- 补充真实 Word 已写入步骤在取消后回滚的测试，确认文档恢复且能够观察到 `Cancelled` 事件。
- 修正原有取消测试的弱断言，不再把抛出 `OperationCanceledException` 当成可接受结果。
- 更新 OPT-P0-002 的状态与已实现功能说明，准确描述覆盖范围和剩余 CI 边界。

## 修改范围

- `src/SmartWord.Application/Orchestration` 中 Agent 运行循环的取消收口逻辑。
- `tests/SmartWord.Application.Tests` 中取消事件、状态和回滚契约的纯逻辑测试。
- `tests/SmartWord.OfficeIntegration.Tests` 中只读文档与真实 UndoRecord 取消回滚测试。
- `docs/优化问题跟踪.md` 和 `docs/已实现的功能.md` 中 OPT-P0-002 状态说明。

## 不在范围

- 不新增或采购带 Microsoft Office 的 CI Runner，不改变 Office 授权与交互式桌面环境配置。
- 不扩展到脚注、尾注、批注、文本框、内容控件等更多复杂 Word 对象。
- 不改变 Ask、Plan、Agent 的工具权限定义和前端事件协议。
- 不伪造工具调用或模型推理轨迹。

## 实现方案

1. 梳理 `AgentOrchestrator.RunAsync` 中取消可能发生的位置，保留当前写步骤 UndoScope 和 Todo 回滚逻辑，在运行循环边界统一捕获取消。
2. 将调用方请求取消规范化为 `AgentEventType.Cancelled`，随后正常结束异步事件流；非调用方触发的超时继续按工具超时或错误处理，避免误报用户取消。
3. 保留 `WebViewBridge` 的取消异常捕获作为宿主兜底，但正常取消路径应由编排器事件到达前端。
4. 调整确认阶段取消测试，断言恰好一条 `Cancelled` 事件且无写入。
5. 使用真实 Word 以 `ReadOnly: true` 打开临时 docx，执行 Agent 写入请求，断言 `DocumentNotWritable`、无写工具调用且文件内容未改变。
6. 构造可控的真实 Word 写入后取消路径：在同一写步骤内完成文档修改后触发调用方取消，使编排器执行 UndoRecord 回滚；断言原始文本恢复、取消事件存在且 Word 进程正常清理。
7. 将问题状态从相互矛盾的“未开始/已完成首版基线”修正为已完成本地真实 Word 基线，同时保留无 Office CI 不运行该组测试的环境边界。

## 测试计划

- 运行 `./build.ps1 -Core`，验证应用层取消契约及全部非 Word 自动化测试。
- 运行 `./build.ps1 -WordIntegration`，验证真实 Word 场景全部通过且测试进程退出。
- 运行 `./build.ps1 -AddIn`，验证 VSTO 宿主与 Bridge 编译通过。
- 运行 `git diff --check`，检查空白和编码问题。

## 风险与注意事项

- C# 异步迭代器不能在 `catch`/`finally` 中随意 `yield return`，取消事件的统一收口需要通过循环外状态或拆分内部执行方法实现，避免破坏现有清理逻辑。
- 取消可能发生在 LLM、确认等待、工具执行或写后验证阶段；只应把调用方 CancellationToken 的取消映射为用户取消。
- Word UndoRecord 对异步取消时机敏感，测试必须在 STA 消息循环中执行，并避免用不确定的固定延时判断写入是否已经发生。
- 只读文档可能因 Word 版本、受保护视图或首次启动配置表现不同，断言应基于 `DocumentStatus` 和文档内容，不依赖本地化弹窗文本。
- 真实 Word 测试仍要求安装桌面版 Word，并只清理可唯一确认归测试拥有的进程。

## 实现结果

- 已将 `RunAsync` 拆为公开取消收口层和内部编排流；调用方取消会在内部清理完成后产生单一 `Cancelled` 事件。
- 已修复工具执行与超时等待的取消竞争，取消优先进入当前写步骤 Undo 回滚；Todo、审计和遥测状态统一记录为 cancelled。
- 已新增 2 条 Core 取消契约测试、1 条真实只读文档测试和 1 条真实 Word 写入后取消回滚测试。
- `./build.ps1 -Core`：Application.Tests `213 passed`，编译无警告；默认 Word 测试 `12 skipped`。
- `./build.ps1 -WordIntegration`：真实 Word 测试 `12 passed`，测试拥有的 Word 进程已清理。
- `./build.ps1 -AddIn`：VSTO AddIn 构建通过。
