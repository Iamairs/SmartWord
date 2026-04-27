# Skill 能力包管理实现计划

## Step 1：文档规划（已完成）

- 更新 `docs/project_cur.md` 和 `docs/plan_cur.md`。
- 固定首版边界：文件系统 Skill、创建/加载/删除/启停、运行时选择、禁止脚本执行。
- 提交：`docs: 规划Skill能力包管理`

## Step 2：Core 模型与接口（已完成）

- 新增 Skill 模型：
  - `SkillDefinition`
  - `SkillDetail`
  - `SkillResource`
  - `CreateSkillRequest`
  - `SaveSkillRequest`
  - `SkillPromptContext`
- 新增接口：
  - `ISkillStore`
  - `ISkillPromptResolver`
- 扩展 `AgentRunOptions.SelectedSkillNames`。

## Step 3：Infrastructure 文件系统 Store（已完成）

- 新增：
  - `SkillFrontmatterParser`
  - `SkillPathGuard`
  - `FileSystemSkillStore`
- 实现：
  - 扫描内置和用户 Skill。
  - 解析 `SKILL.md` frontmatter。
  - 创建 Skill 模板。
  - 保存用户 Skill。
  - 删除用户 Skill。
  - 启用/禁用状态。
  - 列出 `references/`、`assets/`、`scripts/` 资源路径。
- 安全：
  - 名称白名单。
  - 路径越界检查。
  - 文件大小限制。
  - 明显密钥脱敏。

## Step 4：Application Prompt 注入（已完成）

- 新增 `SkillPromptResolver`。
- `AgentOrchestrator` 注入 resolver。
- `BuildSystemPrompt` 中加入：
  - 可用 Skill 索引。
  - 用户选择或消息显式 `/skill name` / `@name` 匹配到的完整 Skill 正文。
  - SmartWord 专属安全声明：Skill 不能绕过工具权限，不执行 `scripts/`。

## Step 5：AddIn Bridge（已完成）

- DI 注册 `ISkillStore` 和 `ISkillPromptResolver`。
- 新增 bridge：
  - `GetSkillsJson()`
  - `GetSkillDetailJson(string name)`
  - `CreateSkillJson(string requestJson)`
  - `SaveSkillJson(string name, string content)`
  - `DeleteSkillJson(string name)`
  - `SetSkillEnabledJson(string name, bool enabled)`
- `SendMessageAsync` 解析 `selectedSkillNames` 传入 `AgentRunOptions`。

## Step 6：前端 Skill 管理（已完成）

- 新增 `stores/skills.js`。
- 新增 `SkillPanel.vue`。
- Header 增加“Skill”入口。
- 高级执行选项中增加 Skill 选择器。
- 浏览器模式提供模拟 Skill，并支持本地创建/删除/启停。

## Step 7：测试与验证（已完成）

- 新增文件系统 Skill Store 测试：
  - 创建后可加载。
  - 非法名称被拒绝。
  - 删除内置 Skill 被拒绝。
  - 删除路径越界被拒绝。
  - 禁用 Skill 后不进入启用列表。
  - 脚本仅列为资源，不读取执行内容。
- 新增 prompt resolver 测试：
  - 显式选择 Skill 会注入正文。
  - `/skill name` 和 `@name` 会匹配。
  - 未选中时只注入索引。
  - Prompt 包含禁止脚本执行声明。

## Step 8：文档收尾与最终验证（已完成）

- 更新 `docs/已实现的功能.md`。
- 运行：
  - `dotnet build src\SmartWord.Core\SmartWord.Core.csproj`
  - `dotnet build src\SmartWord.Infrastructure\SmartWord.Infrastructure.csproj`
  - `dotnet build src\SmartWord.Application\SmartWord.Application.csproj`
  - `dotnet build src\SmartWord.OfficeIntegration\SmartWord.OfficeIntegration.csproj`
  - `dotnet test tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj`
  - `npm run build`
- AddIn 构建若仍因本机 VSTO targets 缺失失败，记录环境限制。

## 最终验证记录

- `dotnet build src\SmartWord.Core\SmartWord.Core.csproj`：通过。
- `dotnet build src\SmartWord.Infrastructure\SmartWord.Infrastructure.csproj`：通过。
- `dotnet build src\SmartWord.Application\SmartWord.Application.csproj`：通过。
- `dotnet build src\SmartWord.OfficeIntegration\SmartWord.OfficeIntegration.csproj`：通过。
- `dotnet test tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj`：通过，168 个测试全部通过。
- `npm run build`：通过，并刷新 AddIn WebClient 静态资源。
- `dotnet build src\SmartWord.AddIn\SmartWord.AddIn.csproj`：未通过，当前机器缺少 `Microsoft.VisualStudio.Tools.Office.targets`，属于 VSTO/Office 本机环境限制。

## 已提交的原子提交

- `docs: 规划Skill能力包管理`
- `feat: 增加Skill文件系统存储`
- `feat: 将Skill注入Agent提示词`
- `feat: 暴露Skill管理桥接接口`
- `feat: 增加Skill管理面板`
