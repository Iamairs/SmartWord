# P1-3 实施计划

## 1. SQLite 基础设施

- 增加 `Microsoft.Data.Sqlite` 与 `SQLitePCLRaw.bundle_e_sqlite3` 依赖。
- 新增 `SmartWordSqliteDatabase`，负责 `%AppData%\SmartWord\smartword.db` 路径、WAL、busy timeout、foreign key 和 schema migration。
- 创建 `conversation_messages`、`task_runs`、`task_tools`、`task_changes` 表。

## 2. 对话持久化

- 将 `SqliteConversationStore` 从骨架改为真实实现。
- 按文档路径 hash 隔离历史，空路径使用 `__active_document__`。
- 保存用户、助手、工具消息和工具调用 JSON。
- 读取时恢复 `AgentMessage`，供 LLM 上下文继续消费。

## 3. 任务历史存储

- Core 新增任务运行、工具审计、改动审计和详情模型。
- Infrastructure 实现 `SqliteTaskHistoryStore`。
- `CompleteRunAsync` 时重新聚合工具数、改动数和已验证改动数。

## 4. Agent 编排接入

- `AgentOrchestrator` 注入可选 `ITaskHistoryStore`。
- Run 开始时创建 `task_runs`。
- 工具执行结果写入 `task_tools`，审计失败只记录日志，不影响主流程。
- 文档改动事件写入 `task_changes`。
- 正常完成、失败、取消、暂停均调用 `CompleteRunAsync`。

## 5. AddIn Bridge

- DI 将 `IConversationStore` 切换为 `SqliteConversationStore`。
- 注册 `SmartWordSqliteDatabase` 与 `ITaskHistoryStore`。
- 新增 `GetRecentTaskRunsJson(int limit)` 和 `GetTaskRunDetailJson(string taskRunId)`。

## 6. 前端历史面板

- `hostBridge` 增加历史查询方法，浏览器模式提供模拟历史。
- 新增 `stores/taskHistory.js`。
- 新增 `TaskHistoryPanel.vue`。
- `ChatWindow.vue` 头部增加“历史”入口，任务结束后在面板打开时刷新最近历史。

## 7. 验证计划

- `dotnet build src\SmartWord.Core\SmartWord.Core.csproj`
- `dotnet build src\SmartWord.Infrastructure\SmartWord.Infrastructure.csproj`
- `dotnet build src\SmartWord.Application\SmartWord.Application.csproj`
- `dotnet test tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj`
- `npm run build`

## 当前状态

- SQLite schema、对话持久化、任务历史 store、Agent 审计接入、AddIn bridge、前端历史面板已完成编码。
- 待执行构建、测试和前端资源刷新验证。
