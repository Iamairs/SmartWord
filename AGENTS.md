# 仓库指南
## 项目介绍
SmartWord 是一个基于 VSTO（`.NET Framework 4.7.2`）的 Word 插件。

## 项目结构与模块组织

```text
SmartWord.sln
├── src/
│   ├── SmartWord.Core/                    # 领域层（零外部依赖，定义核心模型与接口）
│   ├── SmartWord.Application/             # 应用层（业务逻辑与流程编排）
│   ├── SmartWord.Infrastructure/          # 基础设施层（外部通信、持久化、配置）
│   ├── SmartWord.OfficeIntegration/       # Office 防腐层与动态脚本执行（COM 互操作）
│   └── SmartWord.AddIn/                   # VSTO 宿主工程（生命周期与 UI 桥接）
├── web/                                   # 前端工程（Vue 3 + WebView2）
│   └── SmartWord.WebClient/
└── tests/
    ├── SmartWord.Core.Tests/              # 核心域模型与业务规则的单元测试
    ├── SmartWord.Application.Tests/       # 流程编排与意图路由逻辑的测试
    └── ...
```

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

**重要**：开发之前需认真阅读`README.md`文件中的内容，并根据`README.md`中的提示，找到正在开发的功能对应于的`docs\instructions`目录中的开发文档进行参考。

对于简单功能：可直接修改代码，并进行测试。
对于复杂功能：
step 1：写一个临时的docs/project_cur.md描述当前需求及注意事项；
step 2：基于当前需求，生成一个临时的docs/plan_cur.md，详细规划实现步骤；
step 3：按照plan_cur.md执行，生成代码。并迭代更新docs/plan_cur.md，直到完成全部任务
step 4：进行测试；
step 5：完成完整任务并测试通过后，更新（修改，替换，新增或删除）文件`docs\已实现的功能.md`中的功能。

## 读取文件

文档在Windows系统，均使用**utf-8编码**，且内容为中文。读取文件务必先以**UTF-8**编码读取。

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

为了保持代码历史清晰，请遵循**“原子化提交 (Atomic Commits)”**原则。请不要一次性提交涉及多个需求、多个bug的所有更改，而是要分开提交：

### 提交流程

1. **局部修改**：完成一个具体的代码逻辑（如一个子功能、修复一个 Bug 或一段重构）。
2. **运行测试**：提交前确保相关测试已通过，测试失败禁止提交！
3. **精准添加**：请明确指定某个需求涉及的文件进行 add，例如 `git add src/utils.cs`。**避免使用 `git add .`**，以防混入其他未完成步骤的代码。
4. **规范提交**：执行 `git commit -m "类型(可选作用域): 简短描述"`。

### commit 类型参考
* `feat`: 新功能
* `fix`: 修补 Bug
* `refactor`: 重构（既不修复错误也不添加功能的代码更改）
* `docs`: 文档变更
* `test`: 添加或修改测试用例