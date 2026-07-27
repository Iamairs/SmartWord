# 固化 VSTO 构建环境

## 需求背景

`SmartWord.AddIn` 是传统 .NET Framework 4.7.2 VSTO 工程，必须导入 Visual Studio Office Tools 提供的 `Microsoft.VisualStudio.Tools.Office.targets`。使用 `dotnet build SmartWord.sln` 时，.NET SDK MSBuild 会将 `VSToolsPath` 解析到 SDK 目录，从而误报缺少 VSTO targets。仓库已有首版 `build.ps1`，但仍缺少前端构建入口、完整环境诊断和一致的文档说明，问题跟踪文档中也保留了“当前机器缺少 targets”的过期结论。

## 目标

- 提供稳定的一键构建入口，明确区分 Core、前端、真实 Word 集成测试和 VSTO AddIn 构建。
- 自动发现安装在任意目录的 Visual Studio/MSBuild，并选择实际包含 VSTO targets 的实例。
- 在缺少 .NET SDK、Node/npm、Visual Studio MSBuild、VSTO targets、.NET Framework 4.7.2 targeting pack 或 Word 时给出可执行的中文诊断。
- 明确 VSTO 工程不能使用 `dotnet build`，并记录开发机所需组件和标准命令。
- 在当前环境完成 AddIn 和整套标准构建验证，关闭 `OPT-P0-003` 的环境验证缺口。

## 修改范围

- 增强仓库根目录 `build.ps1`。
- 更新 `README.md` 的构建与环境说明。
- 更新 `docs/优化问题跟踪.md` 的状态和解决记录。
- 更新 `docs/已实现的功能.md` 中过期的构建边界。

## 不在范围

- 不修改 `SmartWord.AddIn.csproj` 的 VSTO Import 路径或公共构建契约。
- 不把 Visual Studio 专属 targets 复制进仓库，也不使用硬编码开发机路径。
- 不安装或修改本机 Visual Studio、Office、Node.js 等外部软件。
- 不建立需要桌面 Office 的云端 CI，也不新增 VSTO/UI 端到端测试。
- 不修改真实 Word 集成测试的业务场景。

## 实现方案

1. 保留 `-Core`、`-WordIntegration`、`-AddIn`、`-All` 参数，新增 `-Frontend` 和 `-Configuration`。
2. 通过 `vswhere.exe` 枚举 Visual Studio 实例，从实际同时包含 `MSBuild.exe` 和 `Microsoft.VisualStudio.Tools.Office.targets` 的实例中选择构建环境；找不到时输出 Visual Studio Installer 组件提示。
3. AddIn 构建前检查 .NET Framework 4.7.2 reference assemblies、VSTO 引用程序集和 Office PIA，并始终调用 Visual Studio MSBuild。
4. 前端构建检查 `node`、`npm`、`package.json` 和关键依赖文件，在需要时使用独立临时缓存执行 `npm ci`，然后执行 `npm run build`。
5. Core 路径继续使用 `dotnet`，执行 Application 构建和非宿主单元测试；真实 Word 测试保持显式入口。
6. `-All` 执行 Core、前端、AddIn 和真实 Word 集成测试，满足完整本地验证；默认无参数仍执行不启动 Word 的 Core 验证。
7. README 记录安装依赖、标准命令、常见报错和 `dotnet build` 的适用边界。

## 测试计划

- 对 `build.ps1` 做 PowerShell 语法解析检查。
- 运行 `build.ps1 -Core`，验证应用层构建和普通测试。
- 运行 `build.ps1 -Frontend`，验证前端产物生成。
- 运行 `build.ps1 -AddIn`，验证自动发现 VSTO targets，并通过 Visual Studio MSBuild 编译 AddIn。
- 运行 `build.ps1 -All`，验证标准完整入口，包括真实 Word 集成测试和进程清理。
- 检查脚本和文档中不存在开发机专属的硬编码 Visual Studio 路径。

## 风险与注意事项

- 真实 Word 集成测试依赖交互式 Windows 会话、桌面版 Word 和首次启动配置，仍不适合普通无 Office CI。
- `npm ci` 会依据 lockfile 重建 `node_modules`；仅在关键依赖缺失时执行，并使用独立临时缓存降低共享缓存锁冲突风险。
- Visual Studio 安装布局可能随版本变化；发现逻辑优先使用 `vswhere` 并通过实际文件存在性判断，不依赖固定盘符。
- 当前主工作区存在其他任务的未提交改动，本任务在独立 worktree 中精准暂存和提交，不得混入这些改动。
