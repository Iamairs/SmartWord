# SmartWord 智能 Word 插件——开发规格说明书

---

## 目录

1. [项目概述与目标](#1-项目概述与目标)
2. [解决方案与项目结构](#2-解决方案与项目结构)
3. [已实现的功能](#3-已实现的功能)
4. [核心运行模式详细规格](#4-核心运行模式详细规格)
5. [Agent 主循环架构](#5-agent-主循环架构)
6. [上下文工程](#6-上下文工程)
7. [LLM 接入层规格](#7-llm-接入层规格)
8. [系统提示词工程模板](#8-系统提示词工程模板)
9.  [工具系统完整规格](#9-工具系统完整规格)
10. [Office 防腐层与 Roslyn 沙箱](#10-office-防腐层与-roslyn-沙箱)
11. [安全护栏与权限模型](#11-安全护栏与权限模型)
12. [错误处理与自愈策略](#12-错误处理与自愈策略)
13. [前端工程规格（Vue 3 + WebView2）](#13-前端工程规格vue-3--webview2)
14. [WebView2 ↔ C# 双向 RPC 通信协议](#14-webview2--c-双向-rpc-通信协议)
15. [分阶段开发里程碑](#15-分阶段开发里程碑)
16. [ADR架构决策记录](#附录-adr架构决策记录)
---

## 1. 项目概述与目标

### 1.1 产品定位

**SmartWord** 是一个运行于 Microsoft Word 内部的智能 AI 副驾（Co-pilot），其核心能力是将用户自然语言指令转化为可撤销的、精准的 Word 文档操作。它对标 Claude Code、Cursor 等 Coding Agent 的核心设计理念：

> **"Think → Act → Observe → Self-Correct"** — 一个不断自我修正的闭环执行体。

与 Chat GPT 插件的本质区别在于：SmartWord 不是一个"提建议的聊天机器人"，而是一个**有手有眼、可执行、可回滚的自治 Agent**，它能直接操纵 Word 文档的 DOM（即 COM 对象树）。

### 1.2 技术栈总览

| 层次 | 技术选型 | 版本 |
|------|---------|------|
| Office 宿主框架 | VSTO Word Add-In | Office 2016+ |
| .NET 运行时 | .NET Framework | 4.7.2 |
| 动态脚本引擎 | Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting`) | 4.x |
| 前端框架 | Vue 3 + Vite | Vue 3.4+, Vite 5+ |
| 前后端桥接 | WebView2 HostObject | Evergreen |
| LLM 协议 | OpenAI Chat Completions API (兼容格式) | - |
| 持久化 | SQLite via System.Data.SQLite | - |
| 日志 | Serilog + 文件 Sink | - |

---

## 2. 解决方案与项目结构

### 2.1 解决方案文件布局

```
SmartWord.sln
├── src/
│   ├── SmartWord.Core/                    # 领域层（零外部依赖，定义核心模型与接口）
│   │   ├── Interfaces/
│   │   │   ├── IAgentOrchestrator.cs      # 定义 Agent 主循环与任务编排契约
│   │   │   ├── ILlmClient.cs              # LLM 客户端接口（支持流式与结构化错误）
│   │   │   ├── IToolRegistry.cs           # 工具注册中心契约（按模式过滤工具）
│   │   │   ├── ITool.cs                   # 统一的工具执行接口
│   │   │   ├── IConversationStore.cs      # 对话历史与状态存储契约
│   │   │   ├── IContextHydrator.cs        # 文档上下文（状态/大纲/光标）水和契约
│   │   │   └── IUndoScope.cs              # 任务级撤销范围契约
│   │   ├── Models/
│   │   │   ├── AgentMessage.cs            # 兼容 OpenAI 格式的消息体（含工具调用元数据）
│   │   │   ├── ToolDefinition.cs          # 提供给 LLM 的工具描述与 Schema
│   │   │   ├── ToolCallResult.cs          # 工具执行结果（含成功状态与受影响段落）
│   │   │   ├── DocumentContext.cs         # 加入文档保护状态（选区/页码/字数/大纲）
│   │   │   ├── DocumentStatus.cs          # 文档可写性状态（受保护/只读等详细原因）
│   │   │   ├── ExecutionPlan.cs           # Plan 模式生成的任务蓝图与待办清单
│   │   │   └── AgentRunOptions.cs         # Agent 运行配置（最大迭代、压缩阈值等）
│   │   └── Enums/
│   │       ├── AgentMode.cs               # Ask / Plan / Agent（前端自动路由）
│   │       └── ToolPermission.cs          # 工具权限级别（只读/写入等）
│   │
│   ├── SmartWord.Application/             # 应用层（业务逻辑与流程编排）
│   │   ├── Orchestration/
│   │   │   ├── AgentOrchestrator.cs       # 任务级 UndoScope + 文档一致性检查（主循环核心）
│   │   │   ├── IntentRouter.cs            # 自动意图分类（Ask/Plan/Agent）
│   │   │   ├── AskModeHandler.cs          # 处理 Ask 模式的只读问答逻辑
│   │   │   ├── PlanModeHandler.cs         # 采访轮次限制 + Plan→Agent 上下文重构
│   │   │   └── AgentModeHandler.cs        # 处理 Agent 模式的自治执行逻辑
│   │   ├── Context/
│   │   │   ├── ContextHydrator.cs         # 组装 DocumentContext，感知文档当前状态
│   │   │   └── ConversationCompressor.cs  # 结构化摘要（含操作历史 JSON，防止 token 超限）
│   │   ├── PromptBuilder/
│   │   │   ├── SystemPromptBuilder.cs     # 读取并构建 Markdown 格式的系统提示词
│   │   │   └── FunctionCallingSchemas.cs  # 维护工具调用的 JSON Schema 定义
│   │   └── Pipeline/
│   │       └── StreamingResponseHandler.cs # 处理 LLM 流式输出与前端事件推送
│   │
│   ├── SmartWord.Infrastructure/          # 基础设施层（外部通信、持久化、配置）
│   │   ├── LlmClients/
│   │   │   ├── OpenAiCompatibleClient.cs  # 兼容 OpenAI API 的 HTTP 客户端
│   │   │   └── LlmClientOptions.cs        # LLM 连接配置（BaseUrl/ApiKey/重试策略）
│   │   ├── Persistence/
│   │   │   ├── SqliteConversationStore.cs  # 文档隔离（按 document_path 持久化对话）
│   │   │   └── Migrations/                # SQLite 数据库表结构迁移脚本
│   │   └── Configuration/
│   │       └── SmartWordSettings.cs       # 用户全局设置（模型选择/确认开关等）
│   │
│   ├── SmartWord.OfficeIntegration/       # Office 防腐层与动态脚本执行（COM 互操作）
│   │   ├── WordWrappers/
│   │   │   ├── WordApplicationWrapper.cs   # 修复死锁隐患（线程调度防腐）
│   │   │   ├── DocumentWrapper.cs          # GetDocumentStatus()（获取特定文档状态）
│   │   │   └── UndoRecordWrapper.cs        # 支持任务级事务复用（包装 Word 撤销栈）
│   │   ├── Scripting/
│   │   │   ├── CSharpScriptExecutor.cs     # 包装 Roslyn 脚本执行逻辑与 GC 回收
│   │   │   ├── ScriptSecurityValidator.cs  # Roslyn SyntaxTree 分析（拦截危险调用）
│   │   │   ├── ScriptContext.cs            # 注入到动态脚本中的上下文状态
│   │   │   └── ScriptGlobals.cs            # 暴露给脚本的全局变量（WordApp, ActiveDoc）
│   │   └── Tools/
│   │       ├── ProbeDocumentTool.cs        # 宏观感知工具（获取文档地图、结构、状态）
│   │       ├── ReadSectionTool.cs          # 定向读取工具（按标题/段落/光标范围读取）
│   │       ├── GrepDocumentTool.cs         # 搜索工具（关键词定位 + 上下文窗口）
│   │       ├── GetSelectionContextTool.cs  # 上下文工具（专门获取用户当前选中文字及周边内容）
│   │       ├── PatchRangeTool.cs           # 安全写入工具（范围级操作：替换/插入/格式）
│   │       ├── ExecuteScriptTool.cs        # 动态写入工具（复杂操作/跨段落逻辑的 C# 脚本）
│   │       └── VerifyChangeTool.cs         # 状态验证工具（写操作后必须主动回读验证结果）
│   │
│   └── SmartWord.AddIn/                   # VSTO 宿主工程（生命周期与 UI 桥接）
│       ├── ThisAddIn.cs                   # 插件入口点与生命周期钩子（Startup/Shutdown）
│       ├── Ribbon/
│       │   └── SmartWordRibbon.cs         # Word 顶部功能区按钮（唤出侧边栏）
│       ├── TaskPane/
│       │   ├── SmartWordTaskPaneControl.cs # 承载 WebView2 控件的 WinForms 容器
│       │   └── WebViewBridge.cs            # WebView2 ↔ C# 双向 RPC 通信协议实现
│       ├── DI/
│       │   └── ServiceLocator.cs          # 依赖注入容器配置与组合根
│       └── Resources/
│           ├── WebClient/                 # 编译后的 Vue 前端静态资源存放目录
│           └── Prompts/
│               ├── SYSTEM.md              # 基础系统指令
│               ├── AGENT.md               # Agent 模式专项指令（工具流转与 C# 规范）
│               ├── PLAN.md                # Plan 模式专项指令（采访原则与输出格式）
│               └── ASK.md                 # Ask 模式专项指令（只读规则与溯源标注）
│
├── web/                                   # 前端工程（Vue 3 + WebView2）
│   └── SmartWord.WebClient/
│       ├── src/
│       │   ├── components/
│       │   │   ├── ChatWindow.vue           # 侧边栏主聊天界面容器
│       │   │   ├── MessageItem.vue          # 单条消息的气泡渲染（支持 Markdown）
│       │   │   ├── ThoughtActionTrace.vue   # 工具调用过程的折叠/展开跟踪卡片
│       │   │   ├── ContentPreviewPanel.vue  # 写操作执行前的二次确认预览面板
│       │   │   ├── ChangesSummaryPanel.vue  # 写操作成功后的改动摘要与跳转面板
│       │   │   ├── ProgressIndicator.vue    # Step N/M 进度展示 (Plan 模式继承的任务进度)
│       │   │   └── CitationAnchor.vue       # 溯源高亮标签（处理 [n] 点击跳转事件）
│       │   ├── stores/
│       │   │   ├── chat.js                  # 对话状态与消息列表的 Pinia Store
│       │   │   └── settings.js              # 用户设置项的 Pinia Store
│       │   ├── bridge/
│       │   │   └── hostBridge.js            # 封装 window.chrome.webview.hostObjects 调用
│       │   └── main.js                      # Vue 应用入口与全局注册
│       ├── vite.config.js                   # Vite 构建配置（输出到 C# Resources 目录）
│       └── package.json
│
└── tests/
    ├── SmartWord.Core.Tests/              # 核心域模型与业务规则的单元测试
    ├── SmartWord.Application.Tests/       # 流程编排与意图路由逻辑的测试
    └── SmartWord.OfficeIntegration.Tests/ # Word COM 操作与工具调用的集成/模拟测试
```

### 2.2 依赖方向规则

```
AddIn → Application → Core
AddIn → OfficeIntegration → Core
AddIn → Infrastructure → Core

# 严格禁止：
# Core 不得引用任何外层
# Application 不得引用 Infrastructure（通过接口注入）
# OfficeIntegration 不得引用 Application
```

---

## 3. 已实现的功能
### 3.1 Word 宿主集成与插件入口
### 3.2 前端侧边栏与基础交互
### 3.3 Ask 模式与只读工具链
### 3.4 LLM 接入、工具调用与模型能力分流
### 3.5 对话存储、配置管理与长期持久化
### 3.6 测试、验证与当前边界

**第三章（已实现的功能）内容详见文件`docs\已实现的功能.md`**

---

## 4. 核心运行模式详细规格
### 4.1 模式自动路由
### 4.2 Ask 模式（只读问答）
### 4.3 Plan 模式（规划蓝图）
### 4.4 Plan→Agent 上下文重构
### 4.5 Agent 模式（自治执行）

**第四章（核心运行模式详细规格）内容详见文件`docs\instructions\Agent_核心引擎规格.md`(L1-L162)，务必在开发这部分内容前仔细阅读**

---

## 5. Agent 主循环架构

### 5.1 循环总体设计
### 5.2 AgentRunContext 配置
### 5.3 AgentEvent 事件流定义
### 5.4 双模型路由策略

**第五章（Agent 主循环架构）内容详见文件`docs\instructions\Agent_核心引擎规格.md`(L163-L507)，务必在开发这部分内容前仔细阅读**

---

## 6. 上下文工程

### 6.1 文档状态感知
### 6.2 初始上下文水和（Context Hydration）
### 6.3 上下文压缩策略（结构化摘要）
### 6.4 消息历史数据结构

**第六章（上下文工程）内容详见文件`docs\instructions\Agent_核心引擎规格.md`(L508-L668)，务必在开发这部分内容前仔细阅读**

---

## 7. LLM 接入层规格

### 7.1 OpenAI Compatible Client
### 7.2 LlmClientOptions

**第七章（LLM 接入层规格）内容详见文件`docs\instructions\Agent_核心引擎规格.md`(L669-L693)，务必在开发这部分内容前仔细阅读**

---

## 8. 系统提示词工程模板

### 8.1 AGENT.md 核心内容框架
### 8.2 PLAN.md 核心内容框架
### 8.3 ASK.md 核心内容框架

**第八章（系统提示词工程模板）内容详见文件`docs\instructions\Agent_核心引擎规格.md`(L694-L760)，务必在开发这部分内容前仔细阅读**

---

## 9. 工具系统完整规格

### 9.1 工具接口定义
### 9.2 检索策略层级规范
### 9.3 工具完整规格（输入输出、权限级别、执行前后流程等）
### 9.4 ToolRegistry（工具注册中心）伪代码

**第九章（工具系统完整规格）内容详见文件`docs\instructions\Office集成与工具系统.md`(L1-L467)，务必在开发这部分内容前仔细阅读**

---

## 10. Office 防腐层与 Roslyn 沙箱

### 10.1 WordApplicationWrapper
### 10.2 UndoRecordWrapper
### 10.3 ScriptSecurityValidator
### 10.4 COM 对象释放规范

**第十章（Office 防腐层与 Roslyn 沙箱）内容详见文件`docs\instructions\Office集成与工具系统.md`(L468-L593)，务必在开发这部分内容前仔细阅读**

---

## 11. 安全护栏与权限模型

### 11.1 权限层级
### 11.2 PermissionGuard
### 11.3 文档保护检测

**第十一章（安全护栏与权限模型）内容详见文件`docs\instructions\Office集成与工具系统.md`(L594-L640)，务必在开发这部分内容前仔细阅读**

---

## 12. 错误处理与自愈策略

**第十二章（错误处理与自愈策略）内容详见文件`docs\instructions\Office集成与工具系统.md`(L641-L660)，务必在开发这部分内容前仔细阅读**


---

## 13. 前端工程规格（Vue 3 + WebView2）

### 13.1 设计约束：VSTO 侧边栏宽度
### 13.2 聊天界面核心组件
### 13.3 进度指示器
### 13.4 改动摘要面板
### 13.5 ContentPreviewPanel.vue
### 13.6 ThoughtActionTrace.vue
### 13.7 流式输出渲染
### 13.8 溯源跳转功能（扩展至全模式）
### 13.9 设置页面

**第十三章（前端工程规格）内容详见文件`docs\instructions\前端视图与通信协议.md`(L1-L118)，务必在开发这部分内容前仔细阅读**

---

## 14. WebView2 ↔ C# 双向 RPC 通信协议

### 14.1 通信架构
### 14.2 WebViewBridge 实现
### 14.3 请求/响应 JSON 协议

**第十四章（WebView2 ↔ C# 双向 RPC 通信协议）内容详见文件`docs\instructions\前端视图与通信协议.md`(L119-L212)，务必在开发这部分内容前仔细阅读**

---

## 15. 分阶段开发里程碑
### Phase 0：技术风险验证
### Phase 1：可运行骨架
### Phase 2：Ask 模式完整可用
### Phase 3：Agent 模式完整可用
### Phase 4：Plan 模式 + 对话持久化
### Phase 5：稳定性 + 前端体验完善

**第十五章（分阶段开发里程碑）内容详见文件`docs\instructions\开发里程碑与交付计划.md`(L1-L388)，务必在开发这部分内容前仔细阅读**

### 15.1 本地验证入口

普通验证不会启动 Word：

```powershell
.\build.ps1 -Core
```

真实 Word 集成测试必须显式运行，并要求本机安装 Word：

```powershell
.\build.ps1 -WordIntegration
```

VSTO AddIn 构建会检查 VS MSBuild 与 Office targets，并自动执行 NuGet restore：

```powershell
.\build.ps1 -AddIn
```

`-All` 按顺序执行上述三类验证。真实 Word 测试只关闭测试自己创建并识别出的 Word 进程，不会清理用户已打开的 Word。


## 附录 ADR架构决策记录

- WebView2 初始化必须在 UI STA 线程执行。
- 正式 Bridge 涉及 Word COM 的逻辑必须防御性回 UI 线程。
- Roslyn 安全采用“静态分析 + 运行时能力收敛”的组合策略。
- `UndoRecord` 由统一编排层管理，不允许业务代码分散控制。
- 正式系统不应把“静态类型的原始 Word COM 对象”直接作为脚本 API 暴露方式，否则会出现 `CS1748` 互操作类型匹配失败。
- `execute_script` 如需直连 Word COM，只能在高权限路径下通过 `object + dynamic` 晚绑定开放。
