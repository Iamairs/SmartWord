# 当前实施计划归档：执行体验优化

## 完成状态

- [x] 阅读 README 与相关开发规格。
- [x] 创建并归档临时需求文档 `docs/project_cur.md`。
- [x] 实现 Core 权限模型与 PermissionGuard 决策。
- [x] 更新工具权限、编排层确认逻辑与取消处理。
- [x] 更新 AddIn 设置、桥接和运行选项。
- [x] 更新前端设置、发送 payload、取消按钮和 reminder 静默处理。
- [x] 调整 SYSTEM / ASK / AGENT / PLAN Prompt。
- [x] 更新权限、Prompt、Todo reminder 相关测试。
- [x] 更新 `docs/已实现的功能.md`。
- [x] 运行后端测试/构建与前端构建。

## 已验证命令

- `dotnet test tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj`
- `dotnet build src\SmartWord.Core\SmartWord.Core.csproj`
- `dotnet build src\SmartWord.Application\SmartWord.Application.csproj`
- `dotnet build src\SmartWord.OfficeIntegration\SmartWord.OfficeIntegration.csproj`
- `dotnet build src\SmartWord.Infrastructure\SmartWord.Infrastructure.csproj`
- `npm run build` in `web\SmartWord.WebClient`
