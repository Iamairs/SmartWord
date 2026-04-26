# P0 产品化与安全基础实施计划

## 总体策略

P0 拆成 5 个可独立提交的实现块。每个块完成后只 `git add` 本块相关文件，避免混入其它未完成修改。实现顺序先后端安全与诊断，再前端设置与交互，最后做体验面板和文档收尾。

## 阶段 0：规划文档

- [x] 阅读 `docs/代办.md` 的 P0 需求。
- [x] 确认当前工作区只有文档改动，没有代码脏改动。
- [x] 写入本轮需求背景、目标、非目标、风险、提交计划。
- [ ] 提交：`docs: 规划P0产品化与安全基础`

## 阶段 1：后端配置安全与诊断

### 1.1 API Key 加密模型

- [ ] 在 `SmartWordSettings` 增加受保护字段：
  - `ProtectedApiKey`
  - `ProtectedApiKeyHeavy`
  - `ProtectedApiKeyLight`
  - 可选：`ApiKeySaved / ApiKeyHeavySaved / ApiKeyLightSaved` 用于前端展示。
- [ ] 保持旧字段兼容：
  - 读取旧 `ApiKey`、`ApiKeyHeavy`、`ApiKeyLight`。
  - 保存时优先把传入明文加密到 protected 字段。
  - 返回前端快照时不回传完整明文 Key。

### 1.2 DPAPI 保护器

- [ ] 新增 `SmartWord.Infrastructure.Configuration.SecretProtector`。
- [ ] 使用 `System.Security.Cryptography.ProtectedData` 和 `DataProtectionScope.CurrentUser`。
- [ ] 保护器提供：
  - `Protect(string plainText)`
  - `Unprotect(string protectedText)`
  - `IsProtectedValuePresent(string value)`
- [ ] 为 `.NET Framework 4.7.2` 项目补充必要引用。

### 1.3 ServiceLocator 设置读写改造

- [ ] `LoadSmartWordSettings` 读取配置后解密到运行时 `ApiKey` 字段。
- [ ] `SaveSettings` 保存前加密明文 Key，落盘对象不包含明文 Key。
- [ ] `GetCurrentSettingsSnapshot` 返回给前端时只携带 mask / saved 状态，不携带完整 Key。
- [ ] 处理前端不修改 Key 时的保存：
  - 若传入为空但已有 protected 字段，则保留旧密钥。
  - 若传入 `********` 一类 mask，不当作新 Key 保存。

### 1.4 连接诊断 bridge

- [ ] 在 `SmartWordBridge` 增加 `TestModelConnection(string settingsJson)`。
- [ ] 诊断内容：
  - 规范化传入设置。
  - 临时构造 `LlmClientOptions`。
  - 对 Ask/Plan/Agent 分别执行 `ResolveModelRoute`。
  - 尝试调用一个固定短文本请求，验证服务端基本连通性。
- [ ] 诊断请求不得包含当前 Word 文档内容。
- [ ] 返回 JSON：
  - `success`
  - `message`
  - `routes`
  - `supportsToolCalling`
  - `usedFallbackModel`
  - `serviceReachable`

### 1.5 提交

- [ ] 运行后端可行构建或至少编译相关项目。
- [ ] 提交：`feat: 加密保存SmartWord密钥并支持连接诊断`

## 阶段 2：前端设置分层与权限说明

### 2.1 settings store

- [ ] `settings.js` 支持：
  - `hasApiKey`
  - `hasApiKeyHeavy`
  - `hasApiKeyLight`
  - `apiKeyDisplay`
  - `connectionTestResult`
  - `isTestingConnection`
- [ ] 保存时处理 mask：不把 mask 当作真实新 Key。

### 2.2 hostBridge

- [ ] 增加 `testModelConnection(settings)`。
- [ ] 浏览器降级模式返回模拟诊断。

### 2.3 设置 UI 分层

- [ ] 从 `ChatWindow.vue` 抽出或重写设置区为基础/高级两层。
- [ ] 基础设置展示：
  - 服务商预设
  - 默认 Base URL
  - 默认 API Key
  - 轻量模型
  - 重量模型
  - 权限模式
  - 测试连接
- [ ] 高级设置折叠展示：
  - 轻量专用 Base URL / API Key
  - 重量专用 Base URL / API Key
  - 自定义系统指令
- [ ] 权限模式增加说明文案。
- [ ] 全自动执行增加一次风险提示。

### 2.4 提交

- [ ] 运行前端构建。
- [ ] 提交：`feat: 重构设置面板基础高级分层`

## 阶段 3：快捷任务与界面去工程化

### 3.1 快捷任务组件

- [ ] 新增 `QuickActionsPanel.vue`。
- [ ] 快捷任务分类：
  - 问文档：总结全文、总结当前章节、解释选区。
  - 改文字：润色选区、压缩选区、扩写选区、改成正式表达。
  - 审文档：检查错别字和病句、文档体检、处理批注。
  - 整格式：统一格式。
- [ ] 每个任务输出：
  - `content`
  - `manualMode`
  - `requiresSelection`
  - `permissionMode` 建议值。

### 3.2 ChatWindow 集成

- [ ] 欢迎语改为产品任务导向。
- [ ] WebView2 / 浏览器环境提示移动到调试/高级区域。
- [ ] 模式选择折叠为“高级执行选项”。
- [ ] 快捷任务点击后复用 `submitMessage` 的请求逻辑。

### 3.3 提交

- [ ] 运行前端构建。
- [ ] 提交：`feat: 增加快捷任务入口并弱化工程化提示`

## 阶段 4：写入确认业务化与选区优先

### 4.1 ContentPreviewPanel

- [ ] 解析 `toolInput`。
- [ ] 对 `patch_range.operations` 生成自然语言操作列表。
- [ ] 对 `execute_script` 标记为脚本类写入。
- [ ] 展示：
  - 业务描述
  - 风险等级
  - 影响范围
  - 操作列表
  - 是否可验证
  - 是否可撤销
- [ ] 原始 JSON/脚本保留为技术详情。

### 4.2 ThoughtActionTrace

- [ ] 工具名旁增加用户化动作名。
- [ ] 默认折叠技术输入/输出。

### 4.3 选区优先 prompt

- [ ] 快捷任务中选区类请求明确写入：
  - 优先读取当前选区。
  - 没有选区时先说明无法安全限定范围。
  - 写入前按当前权限确认。
- [ ] 对选区写入类任务默认走 Agent + ConfirmWrites。

### 4.4 提交

- [ ] 运行前端构建。
- [ ] 提交：`feat: 优化写入确认与选区优先操作`

## 阶段 5：验证与文档收尾

- [ ] 运行 `dotnet build src/SmartWord.Core/SmartWord.Core.csproj`
- [ ] 运行 `dotnet build src/SmartWord.Infrastructure/SmartWord.Infrastructure.csproj`
- [ ] 运行 `dotnet build src/SmartWord.Application/SmartWord.Application.csproj`
- [ ] 运行 `dotnet build src/SmartWord.OfficeIntegration/SmartWord.OfficeIntegration.csproj`
- [ ] 运行 `dotnet test tests/SmartWord.Application.Tests/SmartWord.Application.Tests.csproj`
- [ ] 运行 `npm run build` in `web/SmartWord.WebClient`
- [ ] 若 AddIn 构建因本机 VSTO targets 缺失失败，记录环境限制。
- [ ] 更新 `docs/已实现的功能.md` 中 P0 产品化与安全基础条目。
- [ ] 更新本计划状态。
- [ ] 提交：`docs: 更新P0完成状态`

## 变更文件预期

### 后端

- `src/SmartWord.Infrastructure/Configuration/SmartWordSettings.cs`
- `src/SmartWord.Infrastructure/Configuration/SecretProtector.cs`
- `src/SmartWord.Infrastructure/SmartWord.Infrastructure.csproj`
- `src/SmartWord.AddIn/DI/ServiceLocator.cs`
- `src/SmartWord.AddIn/TaskPane/WebViewBridge.cs`

### 前端

- `web/SmartWord.WebClient/src/components/ChatWindow.vue`
- `web/SmartWord.WebClient/src/components/ContentPreviewPanel.vue`
- `web/SmartWord.WebClient/src/components/ThoughtActionTrace.vue`
- `web/SmartWord.WebClient/src/components/QuickActionsPanel.vue`
- `web/SmartWord.WebClient/src/stores/settings.js`
- `web/SmartWord.WebClient/src/bridge/hostBridge.js`

### 文档

- `docs/project_cur.md`
- `docs/plan_cur.md`
- `docs/代办.md`
- `docs/已实现的功能.md`

## 当前状态

- [x] 已完成需求拆解。
- [x] 已完成详细实施计划。
- [ ] 已完成阶段 0 提交。
- [ ] 已完成阶段 1 后端配置安全与诊断。
- [ ] 已完成阶段 2 前端设置分层。
- [ ] 已完成阶段 3 快捷任务入口。
- [ ] 已完成阶段 4 写入确认与选区优先。
- [ ] 已完成阶段 5 验证与文档收尾。
