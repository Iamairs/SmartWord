# P0 产品化与安全基础实施计划

## 总体策略

P0 按 5 个可独立验证、可独立提交的实现块推进：先补安全与诊断，再重构设置入口，然后产品化主界面，最后优化写入确认和选区优先操作。每个实现块完成后只添加本块相关文件并提交，避免混入其它阶段的半成品。

## 阶段 0：规划文档

- [x] 阅读 `README.md`、`docs/已实现的功能.md`、`docs/代办.md`，确认当前项目能力、P0 目标和边界。
- [x] 确认 P0 范围：
  - 配置向导与模型能力检测。
  - API Key 本地加密与设置分层。
  - 普通用户界面去工程化。
  - 写入确认面板业务化。
  - 权限模式解释与默认策略优化。
  - 选区优先快捷操作。
- [x] 写入 `docs/project_cur.md`，记录需求背景、当前代码现状、目标、非目标、风险与原子提交计划。
- [x] 写入 `docs/plan_cur.md`，拆分阶段、实现任务、验证任务和预期变更文件。
- [x] 提交：`docs: 规划P0产品化与安全基础`
  - Commit：`f312bfa`

## 阶段 1：后端配置安全与诊断

### 1.1 API Key 加密模型

- [x] 在 `SmartWordSettings` 增加受保护字段：
  - `ProtectedApiKey`
  - `ProtectedApiKeyHeavy`
  - `ProtectedApiKeyLight`
- [x] 增加前端展示辅助字段：
  - `HasApiKey`
  - `HasApiKeyHeavy`
  - `HasApiKeyLight`
  - `ApiKeyDisplay`
  - `ApiKeyHeavyDisplay`
  - `ApiKeyLightDisplay`
- [x] 保持旧字段兼容：
  - 读取旧 `ApiKey`、`ApiKeyHeavy`、`ApiKeyLight`。
  - 保存时优先把传入明文加密到 protected 字段。
  - 返回前端快照时不回传完整明文 Key。

### 1.2 DPAPI 保护器

- [x] 新增 `SmartWord.Infrastructure.Configuration.SecretProtector`。
- [x] 使用 `System.Security.Cryptography.ProtectedData` 和 `DataProtectionScope.CurrentUser`。
- [x] 保护器提供：
  - `Protect(string plainText)`
  - `Unprotect(string protectedText)`
  - `IsProtectedValuePresent(string value)`
- [x] 使用 `dpapi:v1:` 前缀区分受保护值。
- [x] 为 `.NET Framework 4.7.2` 项目补充 `System.Security` 引用。

### 1.3 ServiceLocator 设置读写改造

- [x] `LoadSmartWordSettings` 读取配置后解密到运行时 `ApiKey` 字段。
- [x] `SaveSettings` 保存前加密明文 Key，落盘对象不包含明文 Key。
- [x] `GetCurrentSettingsSnapshot` 返回给前端时只携带 mask / saved 状态，不携带完整 Key。
- [x] 处理前端不修改 Key 时的保存：
  - 若传入为空但已有 protected 字段，则保留旧密钥。
  - 若传入 `********` 一类 mask，不当作新 Key 保存。
- [x] 保存前清理 UI 展示字段，避免把脱敏展示状态当成真实配置长期落盘。

### 1.4 连接诊断 bridge

- [x] 在 `SmartWordBridge` 增加 `TestModelConnection(string settingsJson)`。
- [x] 诊断内容：
  - 规范化传入设置。
  - 临时构造 `LlmClientOptions`。
  - 对 Ask/Plan/Agent 分别执行 `ResolveModelRoute`。
  - 尝试调用一个固定短文本请求，验证服务端基本连通性。
- [x] 诊断请求不包含当前 Word 文档内容。
- [x] 返回 JSON：
  - `success`
  - `message`
  - `routes`
  - `supportsToolCalling`
  - `usedFallbackModel`
  - `serviceReachable`

### 1.5 验证与提交

- [x] 运行 `dotnet build src\SmartWord.Infrastructure\SmartWord.Infrastructure.csproj`：通过。
- [x] 提交：`feat: 加密保存SmartWord密钥并支持连接诊断`
  - Commit：`d1925fb`

## 阶段 2：前端设置分层与权限说明

### 2.1 settings store

- [x] `settings.js` 支持：
  - `hasApiKey`
  - `hasApiKeyHeavy`
  - `hasApiKeyLight`
  - `apiKeyDisplay`
  - `connectionTestResult`
  - `isTestingConnection`
- [x] 保存时处理 mask，不把 mask 当作真实新 Key。
- [x] 保存成功后重新合并后端返回的脱敏设置快照。

### 2.2 hostBridge

- [x] 增加 `testModelConnection(settings)`。
- [x] 浏览器降级模式返回模拟诊断，方便前端独立开发和预览。

### 2.3 设置 UI 分层

- [x] 新增 `SettingsPanel.vue`，把设置区从 `ChatWindow.vue` 中拆出。
- [x] 基础设置展示：
  - 服务商预设。
  - 默认 Base URL。
  - 默认 API Key。
  - 轻量模型。
  - 重量模型。
  - 权限模式。
  - 测试连接。
- [x] 高级设置折叠展示：
  - 轻量专用 Base URL / API Key。
  - 重量专用 Base URL / API Key。
  - 自定义系统指令。
- [x] 权限模式增加说明文案。
- [x] 全自动执行增加风险提示。

### 2.4 验证与提交

- [x] 运行 `npm run build`（目录：`web\SmartWord.WebClient`）：通过。
- [x] 运行 `dotnet build src\SmartWord.Infrastructure\SmartWord.Infrastructure.csproj`：通过。
- [x] 提交：`feat: 重构设置面板基础高级分层`
  - Commit：`a2cf06c`

## 阶段 3：快捷任务与界面去工程化

### 3.1 快捷任务组件

- [x] 新增 `QuickActionsPanel.vue`。
- [x] 快捷任务分类：
  - 问文档：总结全文、总结当前章节、解释选区。
  - 改文字：润色选区、压缩选区、扩写选区、改成正式表达。
  - 审文档：检查错别字和病句、文档体检、处理批注。
  - 整格式：统一格式。
- [x] 每个任务输出：
  - `content`
  - `manualMode`
  - `requiresSelection`
  - `permissionMode` 建议值。

### 3.2 ChatWindow 集成

- [x] 欢迎语改为产品任务导向。
- [x] WebView2 / 浏览器环境提示从普通主界面弱化。
- [x] 模式选择折叠为“高级执行选项”。
- [x] 快捷任务点击后复用 `submitMessage` 的请求逻辑。

### 3.3 验证与提交

- [x] 运行 `npm run build`（目录：`web\SmartWord.WebClient`）：通过。
- [x] 提交：`feat: 增加快捷任务入口并弱化工程化提示`
  - Commit：`49583e0`

## 阶段 4：写入确认业务化与选区优先

### 4.1 ContentPreviewPanel

- [x] 解析 `toolInput`。
- [x] 对 `patch_range.operations` 生成自然语言操作列表。
- [x] 对 `execute_script` 标记为脚本类写入。
- [x] 展示：
  - 业务描述。
  - 风险等级。
  - 影响范围。
  - 操作列表。
  - 是否可验证。
  - 是否可撤销。
- [x] 原始 JSON/脚本保留为技术详情。
- [x] 输入不可解析时保留降级展示，不阻塞确认流程。

### 4.2 ThoughtActionTrace

- [x] 工具名旁增加用户化动作名。
- [x] 技术名称仍保留，方便调试和问题定位。
- [x] 技术输入/输出默认保持折叠。

### 4.3 选区优先 prompt

- [x] 快捷任务中选区类请求明确写入：
  - 优先读取当前选区。
  - 没有选区时先说明无法安全限定范围。
  - 写入前按当前权限确认。
- [x] 对选区写入类任务默认走 Agent + ConfirmWrites。

### 4.4 验证与提交

- [x] 运行 `npm run build`（目录：`web\SmartWord.WebClient`）：通过。
- [x] 提交：`feat: 优化写入确认与选区优先操作`
  - Commit：`dadad18`

## 阶段 5：整体验证与文档收尾

### 5.1 已运行验证

- [x] `dotnet build src\SmartWord.Core\SmartWord.Core.csproj`：通过。
- [x] `dotnet build src\SmartWord.Infrastructure\SmartWord.Infrastructure.csproj`：通过。
- [x] `dotnet build src\SmartWord.Application\SmartWord.Application.csproj`：通过。
- [x] `dotnet build src\SmartWord.OfficeIntegration\SmartWord.OfficeIntegration.csproj`：通过。
- [x] `dotnet test tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj`：通过，151 个测试通过。
- [x] `npm run build`（目录：`web\SmartWord.WebClient`）：通过。
- [x] `dotnet build src\SmartWord.AddIn\SmartWord.AddIn.csproj /p:VSToolsPath=`：已尝试，当前机器缺少 VSTO/Office/WebView2 等宿主构建依赖，未能完成 AddIn 真宿主构建。

### 5.2 文档收尾

- [x] 更新 `docs/project_cur.md`，追加实施结果、边界和验证记录。
- [x] 更新 `docs/plan_cur.md`，把计划状态、提交状态和验证状态同步为真实完成结果。
- [x] 更新 `docs/已实现的功能.md`，追加 P0 产品化与安全基础完成说明。
- [x] 更新 `docs/代办.md`，标记 P0 阶段已完成并说明对应提交范围。
- [x] 提交：`docs: 更新P0完成状态`

## 实际变更文件

### 后端

- `src/SmartWord.Infrastructure/Configuration/SmartWordSettings.cs`
- `src/SmartWord.Infrastructure/Configuration/SecretProtector.cs`
- `src/SmartWord.Infrastructure/SmartWord.Infrastructure.csproj`
- `src/SmartWord.AddIn/DI/ServiceLocator.cs`
- `src/SmartWord.AddIn/TaskPane/WebViewBridge.cs`

### 前端

- `web/SmartWord.WebClient/src/bridge/hostBridge.js`
- `web/SmartWord.WebClient/src/stores/settings.js`
- `web/SmartWord.WebClient/src/components/SettingsPanel.vue`
- `web/SmartWord.WebClient/src/components/QuickActionsPanel.vue`
- `web/SmartWord.WebClient/src/components/ChatWindow.vue`
- `web/SmartWord.WebClient/src/components/ContentPreviewPanel.vue`
- `web/SmartWord.WebClient/src/components/ThoughtActionTrace.vue`
- `src/SmartWord.AddIn/Resources/WebClient/index.html`
- `src/SmartWord.AddIn/Resources/WebClient/assets/*`

### 文档

- `docs/project_cur.md`
- `docs/plan_cur.md`
- `docs/代办.md`
- `docs/已实现的功能.md`

## 当前状态

- [x] 已完成需求拆解。
- [x] 已完成详细实施计划。
- [x] 已完成阶段 0 提交。
- [x] 已完成阶段 1 后端配置安全与诊断。
- [x] 已完成阶段 2 前端设置分层。
- [x] 已完成阶段 3 快捷任务入口。
- [x] 已完成阶段 4 写入确认与选区优先。
- [x] 已完成阶段 5 验证与文档收尾内容更新。
- [x] 已完成最终文档收尾提交。
