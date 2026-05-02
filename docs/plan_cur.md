# 当前实现计划：上下文压缩策略重构

## Step 1：配置与预算（已完成）

- 在 `AgentRunOptions` 增加上下文窗口和比例阈值字段。
- 在 `SmartWordSettings` 增加同名用户设置字段，并在设置归一化、克隆、应用、UI 快照中透传。
- 新增 `ContextBudgetPolicy`，负责按比例计算 soft/hard/emergency limit。

## Step 2：压缩流水线（已完成）

- 新增 `LightToolResultPruner`：只裁旧的大工具结果，保护第一条 user、最近 user turns、最近 tool-call chain、写入/验证/回滚/失败相关工具结果。
- 新增 `OversizedToolResultTruncator`：按上下文比例处理单条异常大工具结果。
- 新增 `ProgramHardStateBuilder`：输出模式、文档、Todo、写步骤恢复状态、验证/回滚/修复约束。
- 新增 `LlmHistoryCompactor`：使用 `ILlmClient.ChatCompletionWithToolsAsync` 生成统一 Current Task Summary。
- 新增 `ContextCompactionService`：串联预算、轻裁剪、LLM 压缩、硬状态和 fallback。

## Step 3：编排器接入（已完成）

- 将 `AgentOrchestrator` 中固定阈值压缩逻辑替换为 `ContextCompactionService.CompactIfNeededAsync`。
- 保持现有 `ConversationCompressor` 作为 LLM 压缩失败或无法降到预算时的规则兜底。
- 允许同一轮运行后续再次压缩，不再用单次 `hasCompactedContext` 阻断。

## Step 4：测试（已完成）

- 新增预算策略、轻裁剪、压缩服务测试。
- 调整现有压缩器测试，确保第一条 user 保留。
- 覆盖 LLM compaction 触发、fallback、程序硬状态中的待修复/已回滚描述。

## Step 5：文档（已完成）

- 更新 `docs/已实现的功能.md` 中上下文压缩相关说明。
