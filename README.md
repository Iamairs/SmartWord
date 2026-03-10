# SmartWord

SmartWord 是一个基于 VSTO（`.NET Framework 4.7.2`）的 Word 插件。  
目标是把自然语言指令转成可控的文档操作，并通过“先建议、后确认执行”的方式降低误改风险。

当前主交互是右侧 WebView2 对话侧栏（Vue 3），支持如下模式：

- `Qa`：文档问答（只读，不改文档）
- `Writing`：写作改写
- `Processing`：结构化整理
- `Execute`：执行型任务（通常包含 VBA）

## 当前实现概览

### 已落地能力

1. `Alt+K` 全局热键唤起侧栏（仅当 Word 为前台窗口时生效）。
2. 多会话管理（新建、切换、持久化、历史回放）。
3. 模式自动路由 + 手动锁定模式（自动/问答/写作/处理/执行）。
4. 仅在问答模式触发检索增强（分块、关键词、向量、可选重排）。
5. 写作/处理/执行模式统一走 `PendingAction`，用户确认后才真正修改文档。
6. VBA 动态注入执行（临时模块创建、入口调用、执行后清理）。
7. Undo 分组（优先 `UndoRecord`，失败降级到无分组）。
8. OpenAI 兼容接口 + 本地降级实现（模型与向量均可降级）。
9. 结构化日志（Serilog 滚动文件 + 可选 Debug Sink）。
10. 前端与后端通过 WebView2 JSON-RPC 通信，支持“本轮生成取消”。

### 已知限制

1. Ribbon 上的“对话侧栏”按钮存在，但点击事件未绑定；主入口实际仍是 `Alt+K`。
2. `SmartWord.AddIn` 依赖 VSTO/Office targets，纯 CLI 环境下 `dotnet build SmartWord.sln` 常见失败。
3. `SmartWord.AddIn` 构建前会自动执行 `npm install && npm run build`，需 Node.js/npm 可用。
4. 向量缓存虽然支持分块级增量，但 `DocumentId` 包含文档长度，全文长度变化时可能触发较大范围重建。
5. 分块 ID 当前采用段落序号（`p1/p2/...`），在文档前部插删段落会降低增量命中率。

## 技术架构

```text
SmartWord.sln
├─ SmartWord.Core/           # 领域契约与模型（接口、请求/响应、路由枚举）
├─ SmartWord.Services/       # 业务实现（编排、模型、检索、存储、VBA、日志）
├─ SmartWord.AddIn/          # VSTO 宿主入口与基础设施
├─ SmartWord.AddIn/WebClient # Vue 3 + Vite 前端（WebView2 承载）
└─ SmartWord.Services.Tests/ # MSTest 测试项目
```

关键入口：

- `SmartWord.AddIn/ThisAddIn.cs`：插件启动、依赖装配、热键注册、全局异常日志。
- `SmartWord.AddIn/Infrastructure/TaskPaneManager.cs`：TaskPane 生命周期管理。
- `SmartWord.AddIn/UI/Web/WebChatPaneControl.cs`：WebView2 容器与前端加载。
- `SmartWord.AddIn/UI/Web/WebViewRpcBridge.cs`：前后端 RPC 路由层。
- `SmartWord.Services/Conversation/ConversationOrchestrator.cs`：核心对话编排。

## 端到端执行链路

### 启动阶段

`ThisAddIn_Startup` 主要动作：

1. 读取配置（环境变量 + `runtime-settings.local.json` + 默认值）。
2. 初始化日志与全局异常捕获。
3. 构建 Word 主线程调用器（`WordThreadInvoker`）。
4. 装配模型、向量、检索、路由、会话存储、VBA 执行能力。
5. 创建 `ConversationOrchestrator`。
6. 初始化 `TaskPaneManager`（侧栏按需创建）。
7. 注册全局热键 `Alt+K`。

### 单轮对话

1. 前端调用 `turn.submit`，桥接层映射为 `RunTurnAsync`。
2. 读取当前选区文本（插入点不算有效选区）。
3. 路由服务判定本轮模式。
4. `Qa` 模式：执行文档检索并直接生成答案。
5. 非 `Qa` 模式：生成待执行动作（改写、VBA 或混合）。
6. 写入会话消息与待执行动作，返回给前端展示。

### 确认执行

1. 前端调用 `action.apply`。
2. 编排器查找 `PendingAction`。
3. `Rewrite/Hybrid`：替换当前选区文本。
4. `Vba/Hybrid`：净化代码、注入临时模块、执行入口、清理模块。
5. 标记 `IsApplied=true` 并持久化会话。

## 环境要求

### 运行/调试插件

- Windows
- Microsoft Word 桌面版（支持 COM/VBA）
- Visual Studio 2022（建议安装 Office/SharePoint 开发组件）
- .NET Framework 4.7.2 Targeting Pack
- WebView2 Runtime
- Node.js + npm（用于构建 WebClient）

VBA 执行建议：

1. Word 信任中心启用“信任对 VBA 项目对象模型的访问”。
2. 如企业策略禁用宏或 VBProject 访问，执行模式会失败。

## 快速开始

### 方式一：Visual Studio（推荐）

1. 打开 `SmartWord.sln`。
2. 将 `SmartWord.AddIn` 设为启动项目。
3. `F5` 调试（Word 会以加载 AddIn 的方式启动）。

### 方式二：CLI（仅验证 Core/Services）

```powershell
dotnet build SmartWord.Core\SmartWord.Core.csproj
dotnet build SmartWord.Services\SmartWord.Services.csproj
dotnet test SmartWord.Services.Tests\SmartWord.Services.Tests.csproj
```

说明：

- `SmartWord.AddIn` 的完整构建需要 Office VSTO targets。
- `SmartWord.AddIn.csproj` 内包含 `BuildWebClient` 目标，会在构建前执行 npm 构建。

## 使用说明

1. 在 Word 中按 `Alt+K` 打开 SmartWord 侧栏。
2. 选择模型、Prompt 版本与模式（可留空走自动）。
3. 输入自然语言指令后发送。
4. 问答模式直接返回结果，不修改文档。
5. 写作/处理/执行模式会先给出建议预览。
6. 点击“确认执行”才会实际写回文档。
7. 生成中可执行取消（`turn.cancel`）。

## 配置说明

配置加载入口：`OpenAiApiOptions.LoadFromEnvironment`。  
优先级（高 -> 低）：

1. 环境变量
2. `runtime-settings.local.json`
3. 代码默认值

### 主要环境变量

| 变量名 | 说明 | 默认值 |
|---|---|---|
| `SMARTWORD_API_KEY` | Chat API Key（未配置时会降级本地模型） | 空 |
| `SMARTWORD_API_BASE_URL` | Chat API Base URL | `https://api.openai.com/v1` |
| `SMARTWORD_API_MODEL` | 默认聊天模型 | `gpt-4o-mini` |
| `SMARTWORD_PROMPTS_FILE` | Prompt 目录文件路径 | `Config/prompts.catalog.json` |
| `SMARTWORD_PROMPT_VERSION` | 默认 Prompt 版本 | `prompts.catalog.json.activeVersion` |
| `SMARTWORD_EMBEDDING_MODEL` | Embedding 模型 | `text-embedding-3-small` |
| `SMARTWORD_EMBEDDING_API_BASE_URL` | Embedding Base URL | 继承 Chat Base URL |
| `SMARTWORD_EMBEDDING_API_KEY` | Embedding API Key | 继承 Chat API Key |
| `SMARTWORD_CHAT_STORE_FILE` | 会话存储文件路径 | `Config/chat.sessions.local.json` |
| `SMARTWORD_VECTOR_INDEX_DIR` | 向量索引目录 | `Config/vector-index` |
| `SMARTWORD_SETTINGS_FILE` | 本地配置文件路径 | `Config/runtime-settings.local.json` |
| `SMARTWORD_LOG_LEVEL` | 日志级别 | `Information` |
| `SMARTWORD_LOG_DIR` | 日志目录 | `%LOCALAPPDATA%/SmartWord/Logs` |
| `SMARTWORD_LOG_RETAINED_FILES` | 日志保留文件数 | `14` |
| `SMARTWORD_LOG_FILE_SIZE_BYTES` | 单文件大小上限 | `10485760` |
| `SMARTWORD_LOG_DEBUG_SINK` | 是否输出到 Debug Sink | `false` |

兼容变量：

- `OPENAI_API_KEY`
- `OPENAI_BASE_URL`
- `OPENAI_MODEL`

### 本地配置文件

模板文件：

- `SmartWord.AddIn/Config/runtime-settings.template.json`

建议复制为：

- `SmartWord.AddIn/Config/runtime-settings.local.json`

示例：

```json
{
  "apiBaseUrl": "https://api.openai.com/v1",
  "apiKey": "sk-xxxx",
  "defaultModel": "gpt-4o-mini",
  "availableModels": ["gpt-4o-mini", "gpt-4.1-mini"],
  "promptCatalogPath": "Config/prompts.catalog.json",
  "defaultPromptVersion": "v1",
  "embeddingModel": "text-embedding-3-small",
  "embeddingApiBaseUrl": "",
  "embeddingApiKey": "",
  "chatStorePath": "Config/chat.sessions.local.json",
  "vectorIndexDirectory": "Config/vector-index",
  "logging": {
    "logLevel": "Information",
    "logDirectory": "",
    "retainedFileCountLimit": "14",
    "fileSizeLimitBytes": "10485760",
    "enableDebugSink": "false"
  }
}
```

### Prompt 目录

文件：

- `SmartWord.AddIn/Config/prompts.catalog.json`

要点：

1. `activeVersion` 指定默认版本。
2. `versions[]` 支持多版本共存。
3. 推荐使用 `qa/writing/processing/execute`；兼容 `rewrite/vba` 旧键。
4. 占位符：`{{question}}`、`{{instruction}}`、`{{selected_text}}`、`{{retrieved_context}}`、`{{entry_point}}`。

## 前端（WebClient）说明

目录：

- `SmartWord.AddIn/WebClient`

技术栈：

- Vue 3
- Vite 5

命令：

```powershell
cd SmartWord.AddIn\WebClient
npm install
npm run dev
npm run build
```

说明：

- AddIn 构建时会自动将 `dist` 拷贝到输出目录 `webapp/`。
- `WebChatPaneControl` 会优先从输出目录 `webapp/` 加载，调试兜底读取 `WebClient/dist`。

## 检索与向量索引策略

### 检索触发条件

1. 仅 `Qa` 模式触发检索。
2. `Writing/Processing/Execute` 默认不检索。
3. 自动模式下是否检索取决于路由结果。

### 向量索引构建

1. 不在启动时预构建。
2. 在问答检索链路按需构建/更新（懒加载）。
3. 本地落盘目录默认 `Config/vector-index`。

### 增量行为

1. 同一索引文件内按 `chunkId + textHash` 判断是否复用向量。
2. 会清理已删除分块的旧缓存。
3. 因 `DocumentId` 计算包含内容长度，文档长度变化可能导致切桶重建。
4. 因分块 ID 为段落序号，前部插删段落会影响后续命中率。

## 数据与日志落盘

- 会话文件：`Config/chat.sessions.local.json`
- 向量索引目录：`Config/vector-index`
- 日志目录：默认 `%LOCALAPPDATA%/SmartWord/Logs`
- 日志文件：`smartword-YYYYMMDD.log`

## 测试现状

测试项目：

- `SmartWord.Services.Tests`

当前主要覆盖：

1. `WordSelectionService` 选区边界行为。
2. `CommandRouteService` 基础路由行为。
3. `ConversationOrchestrator` 问答/写作/检索触发/取消流程。

运行命令：

```powershell
dotnet test SmartWord.Services.Tests\SmartWord.Services.Tests.csproj
```

## 开发建议

1. 保持分层边界：`Core` 仅契约模型，`Services` 放实现，`AddIn` 放宿主与 UI。
2. 复杂流程优先集中到 `ConversationOrchestrator`，避免并行入口分叉。
3. Word COM 调用必须通过 `IWordThreadInvoker` 回到宿主主线程。
4. 新增可落盘结构时，优先考虑与现有 JSON 文件格式向后兼容。
