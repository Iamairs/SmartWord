# 当前需求：Skill scripts 执行支持

## 需求目标

为 SmartWord 增加受控的 `skill_run_script` 工具，让 Agent 模式可以执行 Skill 包 `scripts/` 目录下的 C# 或 Python 脚本，用于本地分析、格式转换、术语提取和生成结构化建议。脚本不能直接修改 Word 文档；任何 Word 写入仍必须继续使用 `patch_range` 或 `execute_script` 并走现有确认、Undo、验证和任务历史审计流程。

## 关键约束

- 工具仅在 Agent 模式向模型暴露，Ask / Plan 模式不得暴露或执行。
- 脚本路径必须位于指定 Skill 的 `scripts/` 目录内，拒绝绝对路径、`..` 越界和跨 Skill 访问。
- 支持 `csharp` 与 `python` runtime；首版不支持 Bash、PowerShell、Node。
- Python 从 `PATH` 自动探测解释器，不自动安装依赖，不注入 API Key。
- 默认禁止联网；首版通过静态扫描和收敛环境变量防护，不声称具备内核级沙箱。
- 用户确认的输入路径复制到每次运行的 workspace `inputs/`，脚本只在 workspace 中读写，输出收集自 `outputs/`。
- 授权记忆按 `skillName + relativeScriptPath + scriptHash + runtime + permissionSet` 细化；脚本内容、路径、runtime 或权限变化后必须重新确认。
- 脚本结果必须进入对话历史和 SQLite 任务历史审计。

## 风险点

- .NET Framework 4.7.2 传统 csproj 需要手动维护 Compile 项。
- Roslyn 脚本执行要使用专用 globals，不能复用 Word COM 的 `execute_script` globals。
- Python 只能做应用层防护，无法阻止可信边界之外的所有系统级行为，因此 UI 和 Prompt 必须明确说明安全边界。
- 前端确认通道当前只有布尔确认，需要扩展“记住授权”的语义，同时保持旧写入工具兼容。
