# SmartWord MVP 任务卡（PoC 阶段）

## 0. 使用说明

- 本文档只覆盖 README 中约定的 6 个 MVP 功能点，不包含 Parking Lot 功能。
- 每张任务卡均使用统一结构：`输入`、`处理`、`验收标准`、`产出物`。
- 验收优先于实现细节；只要验收通过，即视为该卡完成。

---

## 1. 任务卡清单

### 卡片 1：MVP-1.1 快捷唤起（P0）

**目标**
- 在 Word 内按下 `Alt+K`，弹出一个居中的简单输入窗口（WinForms/WPF 均可）。

**输入**
- 用户按键：`Alt+K`

**处理**
- `ThisAddIn` 在启动时注册快捷键触发逻辑。
- 触发后打开 `PromptWindow`（单行或多行输入框 + 确认/取消按钮）。
- 记录用户输入文本（指令字符串）。

**验收标准**
- 在 Word 文档中任意位置按 `Alt+K` 能稳定弹窗。
- 弹窗可输入文本并点击确认关闭。
- 若点击取消，不触发后续 AI 或 VBA 流程。

**产出物**
- `SmartWord.AddIn` 中的快捷键注册入口。
- `PromptWindow`（最简 UI）。

---

### 卡片 2：MVP-1.3 选区上下文抓取（P0）

**目标**
- 获取当前选中的文本，与用户指令组合为模型请求数据。

**输入**
- 用户输入的自然语言指令。
- `Word.Application.Selection.Text`。

**处理**
- 在执行前校验：若无选区文本（空或仅空白），直接提示用户重新选择。
- 构造写作请求 DTO：`Instruction + SelectedText`。

**验收标准**
- 选中段落时能正确读取文本。
- 未选中文本时有明确提示，不进入模型调用。
- 可在日志/调试输出中看到组装后的请求对象。

**产出物**
- `ISelectionService.GetSelectedText()` 的实现。
- `EditorRewriteRequest` DTO。

---

### 卡片 3：MVP-1.4 直接文本替换（P1）

**目标**
- 获取模型返回文本后，直接覆盖当前选区。

**输入**
- 模型返回字符串（润色结果）。

**处理**
- 调用 `SelectionService.ReplaceSelection(string)`。
- 最小异常处理：若替换失败，提示“替换失败，请重试”。

**验收标准**
- 替换后文档中选区内容被新文本覆盖。
- 替换过程中不改变未选中的其他内容。
- 模型返回空字符串时不执行替换并提示用户。

**产出物**
- `ISelectionService.ReplaceSelection()` 的实现。
- `EditorAgentOrchestrator` 中写作链路闭环。

---

### 卡片 4：MVP-2.1 意图转代码（NL2VBA）（P0）

**目标**
- 用户输入排版意图，模型只返回可执行 VBA 代码。

**输入**
- 用户自然语言排版指令（例如“把所有红色字改成加粗黑体”）。

**处理**
- 组装固定 System Prompt，明确约束：
- 仅返回 VBA 代码，不允许解释文本。
- 必须包含可执行入口（例如 `Sub SmartWord_Run()`）。
- 若模型输出包含代码块标记（```），先清洗再交给执行器。
- 对返回结果做最小校验：包含 `Sub`/`End Sub`。

**验收标准**
- 同一条意图可稳定得到可解析 VBA 文本。
- 非 VBA 响应可被识别并阻断执行，给出提示。
- 调试输出可查看原始返回与清洗后结果。

**产出物**
- `IModelService.GenerateVbaCode()` 的实现。
- `VbaCodeSanitizer`（清洗 + 最小结构校验）。

---

### 卡片 5：MVP-2.2 动态注入与执行（P0）

**目标**
- 把模型生成的 VBA 注入临时模块、执行、删除模块。

**输入**
- 清洗和校验后的 VBA 代码字符串。

**处理**
- 通过 `VBProject.VBComponents.Add` 创建临时标准模块。
- 写入 VBA 代码。
- `Application.Run("SmartWord_Run")` 执行入口过程。
- 无论执行成功或失败，最终都删除临时模块（`finally` 保障）。

**验收标准**
- 已启用“信任对 VBA 项目对象模型的访问”时可成功执行。
- 执行后在 VBA 工程中不残留临时模块。
- 执行失败时也不会残留临时模块。

**产出物**
- `IVbaExecutor.Execute(string vbaCode, string entryPoint)` 实现。
- `VbaModuleManager`（创建/删除临时模块的封装）。

---

### 卡片 6：MVP-2.3 安全撤销 Undo（P0）

**目标**
- 动态执行 VBA 的影响必须可被用户 `Ctrl+Z` 一键撤销。

**输入**
- 待执行 VBA 代码字符串。

**处理**
- 在执行 VBA 前后使用 `Application.UndoRecord` 包裹事务。
- 命名撤销记录（例如 `SmartWord AI Format`）。
- 若 Word 版本不支持或对象不可用，降级为普通执行并提示风险。

**验收标准**
- 执行一次 AI 排版后，按 `Ctrl+Z` 可以撤销本次改动。
- 连续执行多次时，撤销行为符合预期（每次可回退一步）。
- 不支持 `UndoRecord` 的环境有清晰提示。

**产出物**
- `IUndoScopeFactory` 与 `WordUndoScope` 实现。
- `VbaExecutor` 中集成 Undo 包裹逻辑。

---

## 2. 三层项目接口草图（最小可行）

> 目标：先让职责分离，避免 `ThisAddIn.cs` 过载。  
> 原则：AddIn 只做入口与 UI；Core 放契约与 DTO；Services 放具体实现。

### 2.1 SmartWord.AddIn（入口层）

**职责**
- 监听 Word 事件与快捷键。
- 弹出输入窗口并获取用户指令。
- 调用 orchestrator，不直接写模型请求或 COM 细节。

**建议类**
- `ThisAddIn`：启动注册、依赖组装。
- `ShortcutController`：管理 `Alt+K` 触发。
- `PromptWindow`：输入指令 UI。
- `EditorCommandHandler`：处理写作辅助链路调用。
- `FormatCommandHandler`：处理 NL2VBA 链路调用。

### 2.2 SmartWord.Core（契约层）

**职责**
- 定义跨层接口、请求/响应模型、错误模型。
- 不依赖 Office Interop 和具体 HTTP 实现。

**建议接口草图**

```csharp
namespace SmartWord.Core.Abstractions
{
    public interface ISelectionService
    {
        string GetSelectedText();
        void ReplaceSelection(string newText);
    }

    public interface IModelService
    {
        string RewriteText(EditorRewriteRequest request);
        string GenerateVbaCode(VbaGenerationRequest request);
    }

    public interface IVbaExecutor
    {
        void Execute(string vbaCode, string entryPoint);
    }

    public interface IUndoScope : IDisposable
    {
    }

    public interface IUndoScopeFactory
    {
        IUndoScope Begin(string name);
    }

    public interface INotificationService
    {
        void Info(string message);
        void Error(string message);
    }
}
```

```csharp
namespace SmartWord.Core.Models
{
    public sealed class EditorRewriteRequest
    {
        public string Instruction { get; set; }
        public string SelectedText { get; set; }
    }

    public sealed class VbaGenerationRequest
    {
        public string Instruction { get; set; }
        public string EntryPoint { get; set; } = "SmartWord_Run";
    }
}
```

```csharp
namespace SmartWord.Core.Orchestration
{
    public interface IEditorAgentOrchestrator
    {
        void RunRewrite(string instruction);
    }

    public interface IVbaAgentOrchestrator
    {
        void RunFormatting(string instruction);
    }
}
```

### 2.3 SmartWord.Services（实现层）

**职责**
- Office/COM 交互实现。
- 模型 API 调用实现。
- VBA 清洗、校验、注入执行、异常包装。

**建议实现类**
- `WordSelectionService : ISelectionService`
- `OpenAiModelService : IModelService`
- `VbaExecutor : IVbaExecutor`
- `WordUndoScopeFactory : IUndoScopeFactory`
- `MessageBoxNotificationService : INotificationService`
- `VbaCodeSanitizer`（工具类）
- `EditorAgentOrchestrator : IEditorAgentOrchestrator`
- `VbaAgentOrchestrator : IVbaAgentOrchestrator`

---

## 3. 最小调用时序（开发顺序）

### 3.1 写作辅助链路（MVP-1.1/1.3/1.4）

1. `Alt+K` -> `PromptWindow` 返回指令。  
2. `EditorAgentOrchestrator.RunRewrite(instruction)`。  
3. `ISelectionService.GetSelectedText()` 读取选区。  
4. `IModelService.RewriteText(request)` 调用模型。  
5. `ISelectionService.ReplaceSelection(result)` 覆盖选区。  
6. 失败则 `INotificationService.Error()`。

### 3.2 排版链路（MVP-2.1/2.2/2.3）

1. 输入排版指令 -> `VbaAgentOrchestrator.RunFormatting(instruction)`。  
2. `IModelService.GenerateVbaCode()` 获取代码。  
3. `VbaCodeSanitizer` 清洗和结构校验。  
4. `IUndoScopeFactory.Begin("SmartWord AI Format")` 开启撤销范围。  
5. `IVbaExecutor.Execute(vba, "SmartWord_Run")` 注入执行删除。  
6. 异常统一提示“代码执行失败，请换一种说法”。

---

## 4. PoC 阶段完成定义（DoD）

- 6 张卡全部通过手工验收。
- 两条链路各至少完成 3 次稳定演示（不同文档内容）。
- 任一失败场景都有用户可理解提示，不出现静默失败。
- VBA 执行流程在成功与失败后都不残留临时模块。
- 文档中明确记录环境前置条件（Office 版本、宏安全设置）。

