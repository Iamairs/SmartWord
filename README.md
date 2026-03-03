📦 SmartWord v1.0 MVP 功能清单

模块一：写作辅助 (Basic Editor Agent)

目标：跑通 C# 抓取 Word 内容，并调用大模型 API 返回结果的基本链路。

功能点 ID功能名称优先级MVP 阶段实现标准 (Scope)MVP-1.1快捷唤起P0按下 Alt+K，在屏幕中央弹出一个简单的 WinForm/WPF 输入框（无需做到完美的无边框悬浮和跟随光标）。MVP-1.2基础指令输入P0用户输入自然语言指令（如：“帮我润色这段话，语气更正式一点”）。MVP-1.3选区上下文抓取P0极简版感知：C# 仅抓取用户当前高亮选中的文本（Selection.Text）和指令一起发给大模型。（砍掉：获取文档元数据和自动抓取前后段落）。MVP-1.4直接文本替换P1大模型返回结果后，直接覆盖替换掉用户选中的文本。

模块二：核心 VBA 引擎 (Core VBA Agent Engine)

目标：跑通产品的核心护城河——动态注入并执行代码。

功能点 ID功能名称优先级MVP 阶段实现标准 (Scope)MVP-2.1意图转代码 (NL2VBA)P0用户输入排版指令（如：“把所有红色的字改成加粗的黑体”），系统在后台组装特殊的 System Prompt，要求大模型仅返回标准的 VBA 代码。MVP-2.2动态注入与执行P0C# 接收到大模型返回的 VBA 字符串后，通过 VBProject.VBComponents.Add 动态创建一个临时模块，写入代码，调用 Application.Run 执行，然后删除模块。MVP-2.3安全撤销 (Undo)P0将动态执行的 VBA 宏包裹在 Word 提供的 Application.UndoRecord 中。这极其重要，因为大模型写的 VBA 可能搞乱文档，必须让用户能按 Ctrl+Z 一键恢复。MVP-2.4极简报错提示P1砍掉复杂的“自愈循环”。如果在执行 VBA 时 C# 捕获到了 COMException，直接在界面上弹窗提示用户：“AI 生成的排版代码执行失败，请尝试换一种说法描述。”🗑️ 哪些功能被送进了“停车场” (Parking Lot) 及原因

为了让你专注，以下功能在 v1.0 阶段被严格禁止开发，它们将被放入后续版本的规划池中：

M4 本地 RAG 引擎（全砍）：涉及文档智能切片、本地 Embedding 模型集成、SQLite/向量库本地部署。技术跨度太大，直接延期至 v2.0。

M2 文档合规编译器（全砍）：基于正则的检查比较鸡肋，基于语义的检查依赖 RAG。暂时用不到，延期至 v1.5。

自愈循环 (Self-Healing)：虽然是绝佳的卖点，但对于 0 经验开发者，处理 C# 异步等待大模型重试、死循环熔断机制等非常痛苦。v1.0 允许 AI 犯错，只要能 Undo 就行。

跨文件静默扫描 (OpenXML)：极易引发文件被占用、多线程死锁等问题。

## 大模型 API 配置（OpenAI 兼容）

当前项目已支持 OpenAI 兼容接口，配置方式为**环境变量**，不会把密钥写入仓库文件。

### 1) 必填变量

- `SMARTWORD_API_KEY`：你的 API Key（必填）

### 2) 可选变量

- `SMARTWORD_API_BASE_URL`：默认 `https://api.openai.com/v1`
- `SMARTWORD_API_MODEL`：默认 `gpt-4o-mini`
- `SMARTWORD_PROMPT_VERSION`：默认使用 Prompt 配置中的 `activeVersion`
- `SMARTWORD_SETTINGS_FILE`：可指定本地设置文件路径
- `SMARTWORD_PROMPTS_FILE`：可指定 Prompt 目录文件路径

### 3) 快速示例（PowerShell）

```powershell
$env:SMARTWORD_API_BASE_URL = "https://api.openai.com/v1"
$env:SMARTWORD_API_MODEL = "gpt-4o-mini"
$env:SMARTWORD_API_KEY = "sk-xxxx"
$env:SMARTWORD_PROMPT_VERSION = "v1"
```

### 4) 文件配置（推荐）

可在本地创建（不要提交）：

- `SmartWord.AddIn/Config/runtime-settings.local.json`

示例结构：

```json
{
  "apiBaseUrl": "https://api.openai.com/v1",
  "apiKey": "sk-xxxx",
  "defaultModel": "gpt-4o-mini",
  "availableModels": ["gpt-4o-mini", "gpt-4.1-mini"],
  "promptCatalogPath": "Config/prompts.catalog.json",
  "defaultPromptVersion": "v1"
}
```

Prompt 版本目录文件：

- `SmartWord.AddIn/Config/prompts.catalog.json`

你可以新增 `versions` 节点并切换 `activeVersion` 来做版本评测，也可在命令中临时指定。

### 5) 命令行指令约定（Alt+K 输入框）

- 默认：写作改写链路
- `/vba xxx`：VBA 排版链路
- `/model <modelName> xxx`：临时指定模型
- `/prompt <version> xxx`：临时指定 Prompt 版本

可组合示例：

```text
/vba /model gpt-4o-mini /prompt v1_strict 把全文字号改为 14
```

### 6) 安全说明

- 仓库已忽略 `.env` 和 `secrets.json`。
- 仓库已忽略 `SmartWord.AddIn/Config/runtime-settings.local.json`。
- 请勿把真实密钥写入 `README.md`、源码文件、提交记录。
- 可参考仓库根目录 `.env.example`，复制为本地 `.env` 使用（`.env` 不会提交）。
