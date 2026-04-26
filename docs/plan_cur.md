# Todo List

- [x] P0 核对现有暂停恢复链路与前端交互入口
- [x] P1 在 TodoBoard 恢复决策中新增 `SkipCurrentTodo`
- [x] P2 在 TodoManager 中实现跳过当前 Todo 并推进下一步
- [x] P3 将暂停面板简化为继续尝试、跳过此步骤、停止任务
- [x] P4 让停止任务清理当前文档暂停 Todo Board
- [x] P5 同步 Agent 暂停提示文案，移除重建/丢弃表述
- [x] P6 补充并运行后端测试
- [x] P7 构建前端资源并更新宿主静态文件
- [x] P8 更新已实现功能文档
- [x] P9 精准提交本需求相关文件

# 实施计划：写步骤失败暂停决策简化

## 1. 目标

把写步骤连续失败后的暂停体验从“系统恢复决策”改成“用户任务决策”。用户只需要理解：

1. 继续尝试当前步骤。
2. 跳过当前失败步骤，继续后面的任务。
3. 停止本次任务，不再保留这个暂停任务板。

暂停面板不再展示 `按当前计划重建` 和 `丢弃并新建空板`，也不新增“更多选项”。

## 2. 后端改动

1. 在 `TodoBoardRecoveryDecision` 增加 `SkipCurrentTodo`。
2. 在 `TodoManager.ResolveRecoveryAsync` 增加 `SkipCurrentTodo` 分支：
   - 如果存在 in-flight 写步骤，先恢复最近可信任务板快照。
   - 找到第一个 `InProgress` Todo，找不到则使用第一个 `Pending` Todo。
   - 将该 Todo 标记为 `Skipped`，写入完成时间和更新时间。
   - 自动推进下一条 `Pending` Todo 为 `InProgress`。
   - 清理暂停/恢复原因和最近错误，并刷新可信快照。
3. 在 `AgentOrchestrator` 中把暂停原因统一改成“继续尝试 / 跳过此步骤 / 停止本次任务”。
4. 在 `WebViewBridge` 中支持 `skip_current_todo` 决策，并新增停止暂停任务的清理入口。

## 3. 前端改动

1. `TodoBoardPausePanel` 只保留三个按钮：
   - `继续尝试` -> `recover_existing`
   - `跳过此步骤` -> `skip_current_todo`
   - `停止任务` -> `stop_task`
2. `ChatWindow` 针对不同暂停决策生成更明确的继续提示。
3. `stop_task` 不再启动 Agent 主循环，而是调用宿主清理当前文档 Todo Board，然后关闭暂停面板。
4. `hostBridge` 新增 `stopPausedTodoRun`，浏览器预览模式下提供 no-op 模拟。

## 4. 验证

1. `dotnet test tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj --no-restore`
2. `npm run build`，确认 Vue 前端能正常打包到 AddIn WebClient 静态资源。

## 5. 注意事项

- 本轮只改写步骤失败后的暂停面板，不删除启动异常恢复面板。
- `按当前计划重建` 和 `丢弃并新建空板` 后端能力继续保留，供启动恢复和兼容旧协议使用，但不再出现在暂停面板。
- 当前工作区可能存在 OfficeIntegration 的无关修改，提交时必须精准添加本需求文件。
