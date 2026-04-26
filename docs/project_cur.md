# P0 产品化与安全基础落地

## 需求背景

`docs/代办.md` 已将 SmartWord 下一阶段改进拆分为 P0-P3。当前用户要求完成 P0 阶段需求，并按项目规范把详细规划写入 `docs/project_cur.md` 与 `docs/plan_cur.md`，同时按原子粒度多次 git 提交。

P0 的核心不是新增高风险 Word 写入能力，而是把已有 Agent 技术底座产品化、安全化：

- 首次配置向导与模型能力检测。
- API Key 本地加密与设置分层。
- 普通用户界面去工程化。
- 写入确认面板改为业务改动确认。
- 权限模式解释与默认策略优化。
- 选区优先快捷操作。

## 当前代码现状

### 后端

- `SmartWordSettings` 当前保存 Base URL、API Key、轻量/重量模型、权限模式、自定义指令。
- `ServiceLocator.SaveSettings` 当前直接 JSON 序列化设置到 `%AppData%\SmartWord\settings.json`，API Key 为明文。
- `LlmClientOptions.ResolveModelRoute` 已能基于模式与模型能力判断工具调用可用性，但没有暴露给前端做显式诊断。
- `WebViewBridge` 已支持 `GetSettingsJson`、`SaveSettingsJson`、`SendMessageAsync`、取消、确认写操作、Todo 恢复决策等 bridge 方法。
- `PermissionGuard` 已实现 `ReadOnly / ConfirmWrites / AutoSafeWrites / FullAuto` 权限语义，其中 `AutoSafeWrites` 下 `execute_script` 仍需确认。

### 前端

- `ChatWindow.vue` 目前集中承载聊天、设置、模式选择、确认、Plan、Todo 面板。
- 设置页直接展示默认/轻量/重量 Base URL、API Key、模型名，信息密度高。
- `ContentPreviewPanel.vue` 当前默认展示工具名、操作描述和原始输入，普通用户仍需理解工具 JSON。
- `ThoughtActionTrace.vue` 当前直接展示工具名和输入/输出。
- `hostBridge.js` 有浏览器降级模拟能力，可扩展设置诊断模拟。

## 实施目标

### 目标 1：配置安全

- API Key 不再明文写入 settings.json。
- 旧明文配置可读取并在再次保存时迁移为受保护格式。
- 前端展示已保存 Key 时默认脱敏，不回显完整密钥。

### 目标 2：连接与模型能力诊断

- 前端可在设置页点击“测试连接”。
- 后端提供 bridge 诊断接口，不发送当前文档内容。
- 诊断结果至少包括：配置是否完整、模型路由结果、是否支持工具调用、是否启用了降级、服务端连通性结果。

### 目标 3：设置页产品化分层

- 基础设置突出服务商、默认 API Key、默认模型、权限模式、测试连接。
- 高级设置折叠展示轻量/重量模型、专用 Base URL/API Key、自定义系统指令。
- 权限模式提供清晰说明，降低用户误开全自动执行风险。

### 目标 4：主界面去工程化与快捷任务

- 首页显示常用任务入口，而不是只提供空聊天框。
- 初始化提示改为产品任务导向。
- WebView2 / 浏览器预览环境提示不再占据普通用户主要视线。
- 模式选择改为高级执行选项，默认仍保持兼容的 `manualMode` 发送。

### 目标 5：写入确认业务化

- `ContentPreviewPanel` 默认展示“将执行什么业务动作、影响范围、风险、是否可验证/可撤销”。
- 原始工具 JSON/脚本保留在技术详情中。
- `patch_range` 尽量解析 operations，形成用户能读懂的操作列表。
- `execute_script` 标记为脚本类高风险路径，提醒用户重点确认描述与影响范围。

### 目标 6：选区优先快捷操作

- 快捷任务中区分选区类任务与全文类任务。
- 选区写入类任务默认使用 Agent + 写入前确认。
- 无法确认选区时在 prompt 中明确要求先读取选区上下文，避免误改全文。

## 非目标

- 不实现 P1 的真实改动前后 diff。
- 不实现 Word 修订模式。
- 不实现长期 SQLite 任务历史。
- 不新增高风险 Word 写入工具。
- 不重构 `AgentOrchestrator` 主循环。
- 不修改 `docs/已实现的功能.md` 作为最终完成记录，直到本轮测试通过后再追加 P0 完成说明。

## 风险与处理

### API Key 兼容风险

- 风险：旧用户 settings.json 中已有明文字段。
- 处理：读取时保留旧字段兼容；保存时写入受保护字段，并清空/不输出明文 Key。

### DPAPI 环境风险

- 风险：DPAPI 只适用于 Windows 用户上下文。
- 处理：项目本身是 VSTO Word 插件，运行环境为 Windows；加密失败时返回明确错误，不静默保存明文。

### 模型诊断成本风险

- 风险：真实请求可能产生少量费用。
- 处理：诊断请求使用固定短文本，不注入文档内容；UI 文案明确“会向模型服务发送一条测试请求”。

### 前端集中组件风险

- 风险：`ChatWindow.vue` 已较大，继续叠加会增加维护成本。
- 处理：新增 `QuickActionsPanel.vue`、`SettingsPanel.vue` 等小组件，把主组件瘦身。

### 构建环境风险

- 风险：本机可能仍缺 VSTO targets，AddIn 真宿主工程无法完整构建。
- 处理：至少运行可构建的 Core/Application/Infrastructure/OfficeIntegration 项目和前端构建；如 AddIn 构建因环境缺失失败，明确记录。

## 原子提交计划

1. `docs: 规划P0产品化与安全基础`
   - 更新 `docs/project_cur.md`
   - 更新 `docs/plan_cur.md`
   - 纳入 `docs/代办.md`
2. `feat: 加密保存SmartWord密钥并支持连接诊断`
   - 后端 DPAPI 保护 API Key
   - bridge 连接诊断接口
   - 相关配置模型与项目文件更新
3. `feat: 重构设置面板基础高级分层`
   - 前端设置 UI 分层
   - 权限说明
   - 测试连接按钮与结果展示
4. `feat: 增加快捷任务入口并弱化工程化提示`
   - 快捷任务面板
   - 产品化欢迎语
   - 高级执行选项折叠
5. `feat: 优化写入确认与选区优先操作`
   - 业务化写入确认
   - patch_range 操作解析
   - 选区快捷任务 prompt 约束
6. `docs: 更新P0完成状态`
   - 更新 `docs/plan_cur.md`
   - 如测试通过，更新 `docs/已实现的功能.md`

## 验收标准

- settings.json 中新保存的 API Key 不以明文出现。
- 旧明文配置能正常读取，保存后迁移到受保护字段。
- 设置页可完成基础配置、测试连接、查看模型工具调用能力。
- 普通用户可以通过快捷任务发起常见 Ask/Plan/Agent 请求。
- 模式选择仍可用，但默认作为高级执行选项呈现。
- 写入确认面板无需展开原始 JSON 也能判断风险与影响范围。
- 前端构建通过。
- 可构建项目和相关测试尽量通过；不能运行的 VSTO 宿主构建需记录环境原因。
