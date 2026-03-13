# 仓库指南
## 项目介绍
SmartWord 是一个基于 VSTO（`.NET Framework 4.7.2`）的 Word 插件。  
目标是把自然语言指令转成可控的文档操作，并通过“先建议、后确认执行”的方式降低误改风险。

当前主交互是右侧 WebView2 对话侧栏（Vue 3），支持如下模式：

- `Qa`：文档问答（只读，不改文档）
- `Writing`：写作改写
- `Processing`：结构化整理
- `Execute`：执行型任务（通常包含 VBA）

## 项目结构与模块组织

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


## 构建、测试与开发命令

在仓库根目录下运行：
```powershell
dotnet build xxx
```
或使用：
```powershell
msbuild xxx
```

## 代码风格与命名规范

* **语言与框架**：C#（传统 csproj 格式，.NET Framework 4.7.2）。
* **缩进与换行**：使用 4 个空格缩进；大括号另起一行（Allman 风格）。
* **命名习惯**：
* 类型/方法/属性：`PascalCase`
* 私有字段：`_camelCase`
* 接口：`I*` 前缀。

* **命名空间**：必须与文件夹路径保持一致（例如 `SmartWord.Services.Vba`）。
* **设计原则**：偏好单一职责的小型类；复杂的业务流程应封装在 `Orchestrator` 类中。
* **注释要求**：必须添加中文代码注释，以确保团队成员能够准确理解代码意图与用法。

---

## 开发规范
对于简单功能：可直接修改代码，并进行测试。
对于复杂功能：
step 1：写一个临时的project_cur.md描述当前需求及注意事项；
step 2：基于当前需求，生成一个临时的plan_cur.md，详细规划实现步骤；
step 3：安装plan_cur.md执行，生成代码。并迭代更新plan_cur.md，直到完成全部任务
step 4：进行测试；
step 5：完成完整任务并测试通过后，清空project_cur.md和plan_cur.md文件内容。

## 测试准则

为了保证插件在复杂的 Word 环境下运行稳定，请遵循以下测试要求：

* **自动化测试**：所有测试代码应存放在独立的 `*.Tests` 项目中，严禁在生产项目中编写测试逻辑。
* **命名规范**：测试方法命名应基于**行为描述**。推荐格式：`方法名_测试场景_预期结果`（例如：`RewriteText_EmptyInput_ReturnsEmptyString`）。
* **关注点分离**：
* **单元测试**：针对 Core 和 Services 中的业务逻辑。由于 VSTO 对象（如 `Microsoft.Office.Interop.Word`）难以模拟，请尽量将逻辑与 Office 对象解耦，以便进行纯逻辑测试。
* **集成测试**：对于必须依赖 Word 实例的逻辑，应确保测试运行后能够正确清理临时文档。
* **重要**：确保单元测试和集成测试覆盖了关键功能，重视单元测试、集成测试和端到端测试的结合使用。
---

## git提交规范

在完成每个功能或修复后，请务必提交清晰、规范的 commit 信息。提交信息应简洁明了，建议采用 `类型: 简短描述` 的格式：

* `feat`: 新功能
* `fix`: 修补 Bug
* `refactor`: 重构（既不修复错误也不添加功能的代码更改）
* `docs`: 文档变更