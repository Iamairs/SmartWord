# SmartWord

SmartWord 是运行在 Microsoft Word 中的 AI 文档副驾。它把自然语言任务连接到当前 Word 文档，帮助用户阅读、总结、检查、规划和执行受控的文档修改。

SmartWord 的核心原则是：先读取文档事实，再执行操作；写入前提供确认（按权限设置）；写入后自动验证；出现错误、取消或文档切换时尽量回滚当前任务。它不是只给建议的聊天机器人，也不会默认获得操作电脑其他文件或网络资源的权限。

> **项目状态**：当前仓库是面向 Windows + Microsoft Word 的开发版本。Ask 只读链路、Agent 写入闭环、Plan 规划、任务板、Skill 管理和基础评测能力已经落地；尚未提供可直接下载的正式安装包。使用前请按本文档自行构建或在团队发布渠道获取经过签名的安装包。

## 目录

- [主要能力](#主要能力)
- [运行前提](#运行前提)
- [快速开始](#快速开始)
- [在 Word 中使用](#在-word-中使用)
- [权限、确认与撤销](#权限确认与撤销)
- [Skill 能力包](#skill-能力包)
- [数据、隐私与安全](#数据隐私与安全)
- [项目结构](#项目结构)
- [开发与验证](#开发与验证)
- [已知边界](#已知边界)
- [文档与信息边界](#文档与信息边界)
- [贡献与许可](#贡献与许可)

## 主要能力

### 文档问答与检索

- 阅读当前活动文档的标题、段落、表格、页眉页脚、批注和选区上下文。
- 总结全文或当前章节，解释选区，查找关键词/正则并返回上下文。
- 回答引用文档内容的问题时提供可点击的段落级溯源；点击引用可跳回 Word 对应位置。
- 事实型请求会优先刷新当前文档证据，避免只根据旧对话历史回答。

### 规划与执行

- **对话交流**：只读回答和文档分析，不修改文档。
- **规划任务**：通过有限轮次提问澄清目标，生成可审阅的任务计划；用户确认后才进入执行。
- **自主执行**：按任务板逐步完成文档修改，并显示工具轨迹、进度、验证结果和改动摘要。
- 常见入口包括总结、选区润色/压缩/扩写、正式化表达、错别字和病句检查、文档体检、批注处理及格式整理。

### 可控的文档写入

- 对简单文本和段落操作使用结构化写入；复杂批量操作使用受控脚本路径。
- 一个任务的 Word 修改纳入任务级撤销范围，写入成功后必须经过验证才视为生效。
- 验证失败时优先回滚当前写步骤并进入修复或暂停决策，不会把未验证结果伪装成完成。
- 支持任务取消、暂停、跳过当前步骤、恢复最近可信任务板和查看任务历史。

### Skill 能力包

- 内置 Skill：`document-finalizer`、`business-report-polish`、`term-consistency-check`。
- 可在侧边栏查看、启用/禁用、创建、编辑和删除用户 Skill；单次任务最多激活 3 个 Skill。
- 支持从本地文件夹、公开 GitHub 仓库或 HTTPS ZIP 导入 Skill，并在导入前展示来源、哈希、资源和风险信息。
- Skill 可包含 `references/`、文本型 `assets/` 和 `scripts/`。脚本默认受限，必须经过独立 SkillHost、静态检查和用户授权；外部 Skill 的脚本默认禁用。

### 配置与可观测性

- 支持 OpenAI 兼容格式的云端或本地模型服务，可分别配置默认、轻量和重量模型。
- 设置页提供连接诊断；诊断使用固定短文本，不读取当前 Word 文档。
- 本地保存对话、任务历史、Todo Board 和 Skill 状态；API Key 保存时使用 Windows DPAPI 保护。
- 提供本地日志和评测遥测基础设施，用于排查请求耗时、工具结果和任务状态；正式用户运行默认不写入独立评测数据库。

## 运行前提

SmartWord 是 VSTO Word 加载项，运行和构建需要 Windows 环境：

- Microsoft Word（开发和真实集成测试建议使用 Office 2016 或更高版本）。
- .NET Framework 4.7.2 Runtime；构建需要对应 SDK/Targeting Pack。
- Visual Studio 2022 或 Build Tools，安装“.NET 桌面开发”和“Office/SharePoint 开发”工作负载。
- Visual Studio Tools for Office Runtime（VSTO Runtime）。
- Node.js LTS（仅构建前端需要）。
- 可访问的 OpenAI 兼容模型服务及其 API Key；也可以连接支持兼容协议的本地服务。

仓库未包含通用安装程序。VSTO 清单签名、部署位置和企业分发策略应由部署方另行配置。

## 快速开始

1. 克隆仓库并进入根目录。
2. 安装上述运行前提，确保 `dotnet`、Node.js、Visual Studio 的 `MSBuild.exe` 和 `vswhere` 可用。
3. 先验证核心项目和自动化测试：

   ```powershell
   .\build.ps1 -Core
   ```

4. 构建 WebView2 前端资源：

   ```powershell
   .\build.ps1 -Frontend
   ```

5. 构建 VSTO AddIn：

   ```powershell
   .\build.ps1 -AddIn
   ```

6. 在 Visual Studio 中打开 `SmartWord.sln`，启动 `src/SmartWord.AddIn` 调试，或使用项目生成的部署产物注册加载项。
7. 打开 Word，在功能区找到 **SmartWord** 并打开侧边栏，在设置页填写模型服务信息后进行连接测试。

仅运行 `build.ps1` 等同于 `-Core`。可以组合入口，例如：

```powershell
.\build.ps1 -Core -Frontend -AddIn -Configuration Release
```

不要使用 `dotnet build SmartWord.sln` 验证 VSTO AddIn：.NET SDK 自带的 MSBuild 不包含 Office targets。AddIn 必须使用本脚本自动发现的 Visual Studio MSBuild 或直接使用 Visual Studio 构建。

## 在 Word 中使用

1. 打开要处理的文档，并将光标放在相关位置；需要局部处理时先选中文本。
2. 打开 SmartWord 侧边栏，选择任务入口或直接描述目标。
3. 对需要文档事实的请求，SmartWord 会读取当前文档的必要范围并在回答中显示引用或工具轨迹。
4. 需要修改文档时，先选择合适的权限模式。写入前确认模式下，确认面板会展示操作目的、影响范围、风险、是否可验证和是否可撤销；原始工具参数属于技术详情，可按需展开。
5. 任务完成后查看改动摘要、验证状态和任务板。若验证失败或任务暂停，根据界面选择重试、跳过当前步骤或停止任务。

选区任务默认优先处理当前选区。没有选区时，系统会提示无法安全限定范围；请明确说明目标范围，避免意外处理全文。

## 权限、确认与撤销

权限设置决定 SmartWord 是否可以修改文档或运行 Skill 脚本：

| 模式 | 行为 | 建议用途 |
| --- | --- | --- |
| 只读模式 | 只回答和读取，不修改文档 | 文档问答、体检、风险分析 |
| 写入前确认 | 每次文档写入前由用户确认 | 默认推荐，适合正式文档 |
| 自动安全写入 | 标准结构化写入可自动执行，脚本写入仍需确认 | 可控的重复性整理 |
| 全自动执行 | 尽量不等待写入确认 | 仅建议在副本或低风险文档中使用 |

以下行为始终受运行时权限和确认链路约束：文档写入、复杂脚本、Skill 脚本和任务状态写入。只读模式不会因为模型请求而自动升级权限。Word 文档处于只读、受保护或不可写状态时，Agent 会在开始写入前停止并提示原因。

任务级 Undo 不能替代保存前备份。处理合同、论文、财务或其他重要文档时，建议先复制文档，并在 Word 中保留可恢复版本。

## Skill 能力包

Skill 是可复用的 Word 工作流说明和资源集合，不等同于任意代码执行权限。管理入口位于侧边栏的 Skill 页面：

- 内置 Skill 不允许删除；用户 Skill 可以编辑、禁用或删除。
- 导入同名 Skill 默认拒绝覆盖；每个导入项会经过路径、压缩包结构、文件数量和容量检查。
- Skill 正文按需加载，`references/` 和文本型 `assets/` 只能通过受控只读流程读取。
- `scripts/` 中的 C# 或 Python 脚本在独立进程中执行，默认禁止联网、进程启动和越界文件访问；脚本不能直接修改 Word。
- 首次运行脚本需要用户授权，可选择“本次允许”或“记住授权”。脚本内容、来源、哈希或能力边界变化后，旧授权会失效。

由于 Skill 脚本防护属于应用层能力，不是完整的操作系统级沙箱，请只启用可信来源的 Skill。

## 数据、隐私与安全

默认数据位置（当前 Windows 用户范围内）：

| 数据 | 位置/用途 |
| --- | --- |
| 应用设置 | `%AppData%\\SmartWord\\settings.json` |
| 对话与任务历史 | `%AppData%\\SmartWord\\smartword.db` |
| Todo Board | `%AppData%\\SmartWord\\todo\\` |
| 用户 Skill | `%AppData%\\SmartWord\\skills\\` |
| 日志 | `%AppData%\\SmartWord\\logs\\` |
| Skill 脚本记住的授权 | `%AppData%\\SmartWord\\skills\\skill-script-approvals.json` |

重要行为说明：

- 模型请求可能包含完成任务所需的文档片段、选区或工具结果；是否发送到哪个服务商取决于你的模型配置。
- API Key 不应写入提示词、Skill、脚本或普通日志。保存设置时会使用 Windows DPAPI 保护，前端只显示脱敏状态。
- 日志和任务历史以摘要、状态和必要的工具信息为主，并对常见密钥字段做脱敏；仍建议不要在文档或 Skill 中放置不必要的机密信息。
- 真实评测使用独立的 `eval.sqlite` 和输出目录，不应与用户正式 `smartword.db` 混用。
- 删除应用数据前请先备份需要保留的对话、任务板和 Skill；项目当前未提供跨设备同步。

## 项目结构

```text
SmartWord.sln
├── src/
│   ├── SmartWord.Core/             # 领域模型和接口
│   ├── SmartWord.Application/     # Agent 编排、上下文、权限和任务流程
│   ├── SmartWord.Infrastructure/  # LLM、配置、SQLite、Skill 文件系统和遥测
│   ├── SmartWord.OfficeIntegration/# Word COM、防腐层、工具和脚本执行
│   ├── SmartWord.SkillHost/        # 独立 Skill 脚本宿主
│   └── SmartWord.AddIn/            # VSTO 生命周期、Ribbon、Task Pane 和 Bridge
├── web/SmartWord.WebClient/        # Vue 3 + Vite + WebView2 前端
├── tests/                           # Application/Office/EvalRunner 测试
├── tools/SmartWord.EvalRunner/      # 真实 Word + 真实 LLM 评测运行器
├── docs/                            # 已实现功能、计划、问题跟踪和开发说明
└── specs/                           # 每项开发任务的变更规格
```

依赖方向保持为 `AddIn → Application/Infrastructure/OfficeIntegration → Core`。Core 不依赖外层；具体实现通过接口注入。

## 开发与验证

### 构建与测试入口

```powershell
# 核心项目、应用层测试和可提纯的 OfficeIntegration 测试
.\build.ps1 -Core

# Vue 前端构建，并将产物输出到 AddIn 资源目录
.\build.ps1 -Frontend

# 使用 Visual Studio MSBuild 构建 VSTO AddIn
.\build.ps1 -AddIn

# 启动真实 Word 实例执行集成测试（默认不运行）
.\build.ps1 -WordIntegration

# 依次执行 Core、Frontend、AddIn 和 WordIntegration
.\build.ps1 -All
```

真实 Word 集成测试需要本机安装 Word，并通过专用实例运行；测试只清理自己创建且可识别的 Word 进程，不会主动关闭用户已经打开的 Word。也可以设置 `SMARTWORD_RUN_WORD_INTEGRATION=1` 显式启用。

前端也可单独运行：

```powershell
cd web/SmartWord.WebClient
npm ci
npm run build
npm test
```

评测运行器位于 `tools/SmartWord.EvalRunner`，用于真实 Word、真实 LLM、运行轨迹和离线评分的端到端验证；它需要额外的 Word、模型服务和测试 case 条件，不属于普通开发必经步骤。

### 开发约定

- 代码和文档统一使用 UTF-8；C# 使用 4 空格缩进和 Allman 大括号风格，类型/方法使用 PascalCase，私有字段使用 `_camelCase`。
- 修改前从主分支创建 `codex/<类型>-<主题>` 分支，并在 `specs/` 新增任务 spec。
- 测试放在独立 `*.Tests` 项目中；提交前运行与修改范围匹配的测试和 `git diff --check`。
- 使用原子化提交，精准暂存本次任务文件；合入主分支通过 Pull Request 完成。

## 已知边界

以下事项当前不应被视为已完成能力或安全承诺：

- 没有通用的一键安装包、自动升级服务或跨设备同步。
- 写入确认目前主要提供业务摘要和影响范围，不是完整的字符级/格式级前后 Diff。
- 尚未集成完整的 Word 修订模式工作流；Undo 和验证不能替代专业审阅流程。
- Skill 脚本是独立进程加应用层静态检查，不是 AppContainer 等 OS 级强沙箱；不自动安装第三方依赖。
- Todo Board 仍以文档级 JSON 文件为主，尚未与任务历史形成强事务一致性。
- 引用跳转当前以段落级定位为主，字符级高亮、复杂版式分页和部分 Word 特殊对象仍需进一步验证。
- 模型能力探测和 OpenAI 兼容协议依赖服务商实现差异；连接测试通过不代表所有工具/usage/reasoning 特性都完全一致。
- 真实 Word 和完整 WebView2 UI 端到端覆盖仍有限，正式文档请先在副本中验证结果。

## 文档与信息边界

README 只公开用户完成安装、配置、使用和风险判断所需的信息。下列内容不在 README 中展开，避免把内部实现细节误当作稳定的用户契约：

| 信息类别 | 向用户透出 | 不向用户透出 |
| --- | --- | --- |
| 产品能力 | 可做的任务、支持的 Word 内容、三种工作模式、Skill 入口 | Agent 主循环分支、模型路由算法和内部状态字段 |
| 文档修改 | 影响范围、风险、确认、验证、撤销、暂停和恢复行为 | 工具 JSON Schema、Undo 实现、COM 调度和验证脚本模板 |
| 模型与网络 | 所用服务商由用户配置、文档片段可能发送给模型服务、连接诊断不读取文档 | 请求构造细节、重试分支、provider 特殊字段和完整请求体 |
| Skill 与脚本 | 来源、启停、脚本风险、授权、默认禁止联网和越界访问 | AST 拦截规则、SkillHost 内部协议、Job Object 实现和完整脚本白名单 |
| 本地数据 | 设置、历史、Todo、Skill、日志的存储位置和清理注意事项 | SQLite 表结构、遥测事件字段、内部日志格式和评测数据库 schema |
| 项目状态 | 已实现能力、已知限制、构建前提和验证入口 | 未完成规划的实现承诺、实验性方案和维护者排障手册 |

- 完整系统提示词、模型路由规则、上下文压缩算法和内部事件顺序。
- 工具 JSON Schema、COM 对象释放模板、Roslyn AST 规则和脚本宿主内部协议。
- 类级别职责拆分、构造函数、数据库表结构、日志字段和评测埋点细节。
- 尚未完成的产品规划、问题清单、实验性方案和仅供维护者使用的调试步骤。

这些内容请查阅：

- [`docs/已实现的功能.md`](docs/已实现的功能.md)：按模块记录已落地能力与当前边界。
- [`docs/instructions/`](docs/instructions/)：Agent、Office 工具、前端协议和里程碑等内部开发说明。
- [`docs/代办.md`](docs/代办.md) 与 [`docs/优化问题跟踪.md`](docs/优化问题跟踪.md)：产品化短板和后续工作。
- [`specs/`](specs/)：具体任务的背景、范围、方案和验证记录。
- [`AGENTS.md`](AGENTS.md)：仓库协作、分支、编码和提交规范，仅供贡献者阅读。

## 贡献与许可

欢迎通过 Issue 或 Pull Request 报告问题、提交改进和补充测试。涉及 Word COM、权限、脚本执行、数据持久化或模型请求的改动，请同时更新对应 spec、测试和文档边界说明。

当前仓库未声明统一开源许可证。除非项目维护者另行授权，请不要将代码、内置 Skill、提示词或构建产物用于再分发；贡献代码前请先确认许可安排。
