# 当前实施计划：Skill scripts 执行支持

- [x] 扩展 Core 契约与模型：脚本详情、运行请求/结果、授权 store、确认通道选项、工具权限。
- [x] 扩展 FileSystemSkillStore：解析 `scripts/` 路径、计算 hash、列出脚本详情。
- [x] 新增脚本执行层：workspace 创建、输入复制、C# / Python runner、安全扫描、stdout/stderr 和超时限制。
- [x] 新增 `skill_run_script` 工具并接入 DI、权限、Agent 确认与授权跳过逻辑。
- [x] 更新前端桥接和确认面板：展示脚本专属确认信息，支持“本次允许”和“记住授权”。
- [x] 更新 Skill 面板：展示 scripts 列表、授权状态并支持撤销授权。
- [x] 更新 Prompt 和已实现功能文档。
- [x] 增加后端测试并运行 `dotnet test`；运行前端构建。
