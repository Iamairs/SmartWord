# Todo List

- [x] P0 定位孤立 tool 消息来源
- [x] P1 将自动验证结果改为 `role=user` 内部观察消息
- [x] P2 保留模型真实工具调用的 `role=tool` 写入
- [x] P3 自动验证失败时向模型暴露验证结论与输出
- [x] P4 补充自动验证协议安全测试
- [x] P5 运行后端测试
- [x] P6 更新已实现功能文档
- [x] P7 精准提交本需求相关文件

# 实施计划：自动验证结果不再写成 tool 消息

## 1. 目标

修复自动验证结果被保存为孤立 `role=tool` 消息的问题，避免下一轮 LLM 请求被协议校验拦截，同时保留模型对验证结果的可见性。

## 2. 实施步骤

1. 在 `AgentOrchestrator` 中新增内部观察消息追加方法。
2. 修改 `ExecuteAutoVerifyAsync`：
   - 自动验证通过时，追加 `[SmartWord 自动验证结果]` 用户观察消息。
   - 自动验证未通过时，追加验证结论和验证输出。
   - 验证工具不可用、缺少验证计划或验证无法执行时，也追加可读原因。
   - 不再对自动验证调用 `AppendToolResultAsync`。
3. 新增自动验证观察消息构建方法，统一输出：
   - 当前写步骤。
   - 验证工具。
   - 验证状态。
   - 验证结论。
   - 验证输出。
   - 下一步要求。
4. 补充测试：
   - 自动验证通过后，下一轮请求不包含 `write-1__auto_verify` tool 消息，并包含内部观察。
   - 自动验证失败后，下一轮请求能看到失败 hint / actual / expected，并且没有孤立 tool。

## 3. 验证

- `dotnet test tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj --no-restore`

## 4. 注意事项

- 本轮不放宽 `OpenAiCompatibleClient` 的 tool 协议校验。
- 本轮不伪造 synthetic assistant tool call。
- 已存在于旧运行内存历史中的孤立 tool 需要重启插件或清理会话才能完全消除；本次修复保证新运行不再继续制造该类消息。
