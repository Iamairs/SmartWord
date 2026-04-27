# SmartWord Skill 能力包管理

## 背景

SmartWord 已具备 Word 文档读取、写入、验证、撤销、任务历史审计和前端侧边栏交互能力。下一步需要引入类似 Claude Code / Codex 的 Skill 机制，让用户能把稳定的文档处理流程沉淀为本地能力包。

主流 coding agent 的 Skill 通常是一个包含 `SKILL.md` 的文件夹，`SKILL.md` frontmatter 中的 `name` 和 `description` 用于发现和触发，正文在需要时加载，`references/`、`assets/`、`scripts/` 作为可选资源。SmartWord 的使用背景不同：它处理 Word 文档，而不是代码仓库，因此首版 Skill 不应成为任意脚本执行入口，而应成为“文档工作流上下文包”。

## 用户目标

- 查看当前可用 Skill。
- 创建自定义 Skill。
- 查看和编辑用户 Skill 的 `SKILL.md`。
- 删除用户 Skill。
- 启用或禁用 Skill。
- 在一次 Ask / Plan / Agent 请求中选择 Skill，让 Agent 按 Skill 的文档工作流处理当前 Word 文档。
- 保证 Skill 不绕过现有权限确认、Undo、验证和 SQLite 审计。

## 非目标

- 不实现 Skill 市场。
- 不实现第三方 Skill 在线安装。
- 不执行 `scripts/` 下脚本。
- 不让 Skill 直接调用 Word COM 或文件系统。
- 不做跨设备同步。
- 不做企业签名验证。
- 不把 Skill 内容写入 SQLite；Skill 仍以文件系统为真实来源。

## 设计原则

- 兼容主流目录结构：`skill-name/SKILL.md`，可选 `references/`、`assets/`、`scripts/`。
- 仅允许用户删除 `%AppData%\SmartWord\skills` 下的用户 Skill。
- 内置 Skill 位于 AddIn `Resources\Skills`，只读、不可删除。
- Skill 名称必须匹配 `^[a-z0-9][a-z0-9-]{0,63}$`。
- `SKILL.md` 最大 64KB，提示词注入时每个 Skill 最多 12KB，避免撑爆上下文。
- 创建 Skill 时使用固定模板，模仿 skill-creator 的渐进披露设计：简洁 `SKILL.md`，详细材料放引用文件。
- 安全优先：`scripts/` 只作为资源显示；提示词明确禁止模型请求执行 Skill 脚本或把脚本作为 Word 修改通道。

## 数据位置

```text
内置 Skill: <AddInBase>\Resources\Skills\
用户 Skill: %AppData%\SmartWord\skills\
用户禁用清单: %AppData%\SmartWord\skills\skills-state.json
```

## 安全边界

- Skill store 只接受规范化 Skill 名，不接受任意路径。
- 删除操作必须验证目标目录位于用户 Skill 根目录内。
- 读取资源时仅列出相对路径，不读取 `scripts/` 内容。
- `SKILL.md` 中疑似密钥写入前会脱敏。
- Frontend 不暴露脚本执行按钮。
- Agent prompt 注入安全规则：Skill 不提供新工具权限；所有 Word 修改仍必须通过 `patch_range` / `execute_script` 等现有工具，并受 PermissionGuard、确认面板、UndoScope、验证和任务历史约束。
