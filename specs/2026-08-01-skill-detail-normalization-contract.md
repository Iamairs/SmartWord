# Skill 详情归一化契约收敛

## 需求背景

前端为兼容旧版 AddIn 宿主，会把 PascalCase 的 Skill 详情响应归一化为 camelCase。当前 `normalizeSkillDetail` 先展开原始响应，再写入 camelCase 字段，因此旧响应中的 `Skill`、`Content`、`Resources`、`Scripts` 会继续残留，造成同一语义存在两套字段名，并增加调用方误用旧契约的风险。

## 目标

1. 兼容读取旧宿主的 PascalCase Skill 详情字段。
2. 归一化结果只保留统一的 camelCase 语义字段。
3. 保留与 Skill 详情核心字段无关的未知扩展字段，避免破坏宿主的向前扩展能力。
4. 增加自动化回归测试，防止双字段契约再次出现。

## 修改范围

- 调整 `hostBridge` 的 Skill 详情归一化逻辑。
- 增加前端 Bridge 契约测试及对应测试命令。
- 刷新 AddIn 内嵌的前端生产构建产物。

## 不在范围

- 修改 C# Bridge 当前输出的 camelCase 契约。
- 修改 Skill 资源、脚本授权或导入逻辑。
- 清理未知扩展字段或递归改写 Skill 摘要对象。

## 实现方案

1. 从原始详情对象中显式解构移除 `Skill`、`Content`、`Resources`、`Scripts`。
2. 将剩余字段作为扩展字段保留，再输出规范化后的 `skill`、`content`、`resources`、`scripts`。
3. camelCase 与 PascalCase 同时存在时继续以 camelCase 为准，保持当前兼容优先级。
4. 使用 Node 内置测试运行器模拟旧宿主响应，断言旧字段被移除、未知字段保留、嵌套资源和脚本被正确归一化。

## 测试计划

- 运行前端 Bridge 契约测试。
- 运行前端生产构建，确认 Vue/Vite 编译通过并刷新 AddIn 静态资源。
- 运行 `git diff --check` 检查补丁格式。

## 风险与注意事项

- 依赖旧 PascalCase 输出字段的非正式调用方将无法继续读取这些别名；这是本次契约收敛的预期行为，正式调用方应统一使用 camelCase。
- 只移除四个已知旧字段，未知字段仍原样保留，以降低未来宿主增加元数据时的兼容成本。
