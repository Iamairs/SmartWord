# 建立真实 Word 集成测试基线

## 需求背景

当前自动化测试主要覆盖应用层、基础设施层和 OfficeIntegration 中可提纯的逻辑，缺少真实 Word COM、UndoRecord、文档保护、文档切换、表格、页眉页脚和进程清理等宿主级验证。Word 插件的可靠性不能只依赖 fake COM 或纯逻辑测试，需要建立可显式运行的真实 Word 集成测试基线。

## 目标

- 新增独立真实 Word 集成测试工程，默认不随普通单元测试启动 Word。
- 测试启动独立 Word 实例，并只清理测试拥有的进程。
- 覆盖 OfficeIntegration 工具层和 AgentOrchestrator 写入闭环。
- 新增本地一键验证脚本，区分 Core 测试、真实 Word 集成测试和 VSTO AddIn 构建。

## 修改范围

- 新增 `SmartWord.OfficeIntegration.Tests` 测试工程与测试基础设施。
- 新增最小 Word 文档夹具生成与测试运行逻辑。
- 新增仓库根目录 `build.ps1` 验证入口。
- 更新 README、已实现功能和优化问题跟踪文档。
- 如真实 Word 测试暴露阻塞缺陷，仅做必要的生产代码修复。

## 不在范围

- 不自动加载 VSTO AddIn。
- 不驱动 WebView2 前端或执行完整 UI E2E。
- 不修改现有前端 Bridge 协议、工具 JSON schema 或用户可见接口。
- 不复用 benchmark 输入作为测试夹具。
- 不清理用户已打开的 Word 进程。

## 实现方案

- 新建 `tests/SmartWord.OfficeIntegration.Tests`，目标框架 `net472`，使用 xUnit。
- 通过 `WordIntegrationFactAttribute` 在未设置 `SMARTWORD_RUN_WORD_INTEGRATION=1` 时跳过测试。
- 通过 `StaWordTestHost` 在专用 STA 线程中执行所有真实 Word COM 操作。
- 通过 `WordTestSession` 管理临时 docx、Word 应用、活动文档、保存、关闭和 COM 释放。
- 通过 `OwnedWordProcessGuard` 由 Word `Application.Hwnd` 定位测试拥有的 WINWORD PID，并在测试结束后验证/清理该进程。
- 通过代码生成专用最小 docx 夹具，避免提交和维护二进制文档。
- 复用现有 OfficeIntegration 工具和 AgentOrchestrator 构造方式，测试真实写入、验证、回滚、只读保护和文档切换。
- 新增 `build.ps1`，提供 `-Core`、`-WordIntegration`、`-AddIn`、`-All` 参数。

## 测试计划

- 运行普通 Core 构建和单元测试，确认默认路径不启动 Word。
- 设置 `SMARTWORD_RUN_WORD_INTEGRATION=1` 后运行真实 Word 集成测试。
- 用 `build.ps1 -WordIntegration` 验证 Word 环境检查、测试运行和进程清理。
- 用 `build.ps1 -AddIn` 验证 VSTO targets 检查和 AddIn 构建诊断。

## 风险与注意事项

- Word COM 测试依赖本机 Office、桌面会话和 STA 线程，不能作为无 Office CI 的默认测试。
- Word 弹窗、受保护视图或首次启动配置可能导致测试失败，应输出可诊断错误。
- 进程清理只能作用于测试拥有的 PID，无法确认归属时必须失败并提示人工检查。
- 当前工作区存在 Benchmark Scorer 相关未提交变更，本任务不得混入这些文件。
