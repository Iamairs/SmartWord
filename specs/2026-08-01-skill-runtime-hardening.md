# Skill 运行时加固与可观测性

## 需求背景

当前 Skill 系统由本地 `SKILL.md` 工作流和 `scripts/` 脚本组成。Markdown 内容会在任务启动时注入 Agent 系统提示词，脚本通过 `skill_run_script` 在 AddIn 进程内执行。现有实现已经具备路径校验、脚本哈希授权、临时 workspace 和基础遥测，但仍存在以下风险和体验问题：

- Python/C# 脚本超时与进程隔离不足，脚本异常可能影响 Word 插件进程。
- Skill 未激活时仍可被 `skill_run_script` 解析和执行。
- 脚本授权键没有完整表达能力范围，输入/输出配额和临时文件清理不足。
- 外部来源 Skill 没有独立的信任状态，脚本默认可进入授权流程。
- `references/` 和 `assets/` 只有目录清单，没有受控读取入口。
- Skill 只能由用户手动选择或显式 `@skill` 激活，缺少低成本的本地自动推荐。
- Prompt 使用固定字符截断，无法按 token 预算渐进加载。
- 现有 Agent 遥测默认使用空 Sink，不能用于本地 Skill 效果分析。

## 目标

在不改变现有 Word 写入权限、Undo、验证和任务审计边界的前提下：

1. 将 Skill 脚本迁移到独立 `net472` 进程执行，修复超时、子进程和资源失控问题。
2. 将当前任务的 Active Skill 固化为运行快照，禁止未激活 Skill 执行脚本或读取资源。
3. 完善脚本信任级别、授权能力键、输入/输出配额和 workspace 生命周期。
4. 外部来源 Skill 的脚本默认禁用；Markdown 仍可预览和使用。
5. 提供受控的 `read_skill_resource` 只读工具。
6. 使用本地规则和触发词进行低成本 Skill 自动推荐，并通过前端可见标签展示。
7. 按 token 预算和 Markdown 章节进行渐进式 Skill Prompt 加载。
8. 启用默认脱敏的本地效果观测，记录 Skill 使用与任务结果，不上传云端。

## 修改范围

### 1. 脚本宿主与执行生命周期

- 新增 `src/SmartWord.SkillHost/`，目标框架为 `.NET Framework 4.7.2`，作为单次脚本调用的独立可执行宿主。
- AddIn 侧通过标准输入/输出传输 JSON 请求和结果；宿主进程不直接接触 Word COM。
- 主进程使用 Windows Job Object 管理宿主及其子进程，至少提供：
  - 30 秒默认执行超时；
  - 进程树终止；
  - 单次输入总量 200 MB；
  - 单次输出总量 100 MB；
  - 输出文件数量 1000；
  - 脚本大小 256 KB；
  - stdout/stderr 各 64 KB。
- Python 等待逻辑改为可取消的进程退出等待；超时、取消和宿主异常都返回结构化失败结果。
- 每次调用结束后清理 workspace；为异常退出留下的目录增加启动时 TTL 清理，默认保留 24 小时。
- 复制输入目录时拒绝符号链接、Junction 和其他 reparse point，并在复制前检查总大小和文件数。
- 本次不实现 AppContainer、WASM 或完整的 OS 级文件系统/网络沙箱；外部脚本仍按不可信代码处理并默认禁用。

### 2. Skill 信任和 Active Skill 快照

- `SkillDefinition` 增加 `TrustLevel`、`ScriptPolicy`、`Source`、内容哈希和推荐元数据。
- 信任级别约定：
  - 内置 Skill：`built_in`，脚本策略为 `prompt`；
  - 用户创建 Skill：`user`，脚本策略为 `prompt`；
  - 外部导入 Skill：`external`，脚本策略默认为 `disabled`。
- frontmatter 增加可选字段 `trust_level`、`source`、`activation.triggers`、`activation.excluded_triggers`、`supported_modes` 和 `required_tools`。
- 未声明来源的现有用户 Skill 兼容地按 `user` 处理；内置根目录由 Store 强制标记为 `built_in`。
- `SkillPromptContext` 生成后形成当前任务不可变的 Active Skill 快照，至少包含名称、版本、内容哈希、脚本哈希、资源白名单和脚本策略。
- `AgentRunOptions` 携带快照或规范化的 Active Skill 名称，`ToolCallCoordinator` 在执行 `skill_run_script` 和 `read_skill_resource` 前强制校验快照。
- Skill 在任务执行中被修改、禁用或脚本哈希变化时，不影响当前任务；下一次任务重新解析。
- 新增 Skill 脚本策略设置接口和前端状态展示；外部 Skill 必须由用户显式开启脚本策略后才能进入授权确认。

### 3. 脚本授权、能力和资源配额

- 授权键绑定：Skill 来源、Skill 名称、Skill 内容哈希、脚本相对路径、脚本 SHA-256、runtime、声明的能力集合、输入根目录和输出策略。
- `arguments_json` 通过脚本声明的 JSON Schema 校验，不直接把全部参数放进长期授权键。
- 涉及路径、网络、进程或高容量输出的参数必须逐次确认；`purpose` 仅作为用户可读说明，不作为安全授权依据。
- `expected_outputs` 从描述字段升级为执行后校验：输出类型、目录、数量和大小必须满足声明。
- `read_skill_resource` 仅支持当前 Active Skill 的 `references/` 和文本型 `assets/`，使用相对路径和大小限制；不允许读取未激活 Skill、绝对路径、链接路径或目录外文件。
- 脚本执行时仅复制 manifest 声明且用户确认过的资源；脚本不能通过资源路径回到 Skill 根目录或 workspace 外部。

### 4. Skill 自动推荐和前端可见标签

- Skill manifest 支持触发词、排除词和支持模式。
- Resolver 使用本地规则评分：显式 Skill 名称最高优先级，触发词精确命中次之，名称/显示名/描述词汇重合用于低权重补充；不额外调用 LLM。
- 高置信度 Skill 自动激活，中等置信度仅推荐，低置信度不打扰用户。
- 新增 `skill_recommendation` AgentEvent，携带推荐名称、分数、原因、最终 Active Skill 和是否来自用户选择。
- WebView 显示本次任务的 Active Skill 标签；标签可关闭，关闭后只对当前任务生效，不修改全局启用状态。
- 用户显式选择始终优先于自动推荐，最多仍为 3 个 Active Skill。

### 5. Token 预算和渐进加载

- `AgentRunOptions` 增加 Skill Prompt 预算配置，默认总预算 8192 tokens，索引预算 800 tokens，单次资源读取预算 2000 tokens。
- 预算根据 `ContextWindowTokens` 动态收缩，但不超过默认上限；使用现有 token 估算能力，不新增外部 tokenizer 依赖。
- Skill 索引只保留最相关的启用 Skill，不再无条件注入最多 30 个描述。
- Active Skill 正文按 Markdown 二级标题拆分，优先加载工作流、约束和输出要求；示例和背景章节按需截断。
- 截断必须保留安全边界和最后的输出要求，不允许从任意字符位置截断关键章节。
- `references/` 和 `assets/` 不自动进入系统 Prompt，按 `read_skill_resource` 逐步读取。
- 遥测记录 Skill Prompt 估算 token、预算和实际加载章节。

### 6. 本地效果观测

- 正式 AddIn 默认启用本地 SQLite/JSONL 观测 Sink，不依赖远端服务。
- 记录 Skill 名称、版本、内容哈希、激活来源、推荐分数、脚本调用、工具失败类型、任务成功/失败、验证结果、耗时和 token 估算。
- 默认不记录文档正文、完整输入文件、API Key、Authorization 头和完整对话；原始工具参数和错误文本只保留脱敏、截断后的摘要。
- 观测写入失败不得影响 Agent 主流程。
- 增加读取本地 Skill 观测摘要的 Bridge 接口，为后续前端效果面板保留扩展点；本次先提供基础状态和最近统计，不实现远程上传和跨设备聚合。

### 7. 持久化和兼容性

- Skill 文件保存采用临时文件写入后原子替换。
- 保存时校验原内容哈希，检测并发修改；保留最近 5 个历史版本用于恢复。
- manifest 增加 `schema_version`、`version`、`min_smartword_version`、`required_tools` 和 `supported_modes`；不兼容 Skill 可查看但不激活脚本。
- 旧版无 manifest 字段的 Skill 继续按现有行为工作，默认补齐 `schema_version: 1` 和 `trust_level: user`。

## 不在范围

- AppContainer、WASM、虚拟机或内核级网络/文件系统沙箱。
- 第三方 Skill 市场、远程安装、企业签名、在线更新和跨设备同步。
- 自动修改用户 `SKILL.md` 或根据一次失败自动生成新 Skill。
- 云端遥测、跨用户经验汇总和文档内容上传。
- 重新设计现有 Word 写入工具、Undo、验证状态机和权限模式。
- 在 Ask/Plan 模式开放脚本执行；`skill_run_script` 仍只在 Agent 模式可见。

## 实现方案

1. 先扩展 Core Skill 模型、AgentRunOptions、AgentEvent 和脚本执行契约，保持现有调用方兼容。
2. 在 Infrastructure 完成 manifest 解析、信任状态、原子保存、历史版本、脚本策略和本地观测存储。
3. 在 OfficeIntegration 中实现宿主客户端、Job Object、workspace 配额/清理和资源读取工具。
4. 新增 SkillHost 项目，实现一次请求一次进程的 JSON 协议，复用现有脚本校验和受控 globals，但不加载 Word COM。
5. 在 Application 编排层创建 Active Skill 快照，强制工具执行前校验，并实现推荐评分和 token 渐进加载。
6. 在 AddIn Bridge 增加 Skill 推荐事件、脚本策略、资源读取和观测摘要接口。
7. 在 Vue 前端增加 Active Skill 可见标签、自动推荐状态、外部脚本策略和基础观测摘要；保持 280px 侧边栏布局。
8. 最后补充单元测试、宿主协议测试、脚本超时/配额测试、Skill Resolver 测试、Bridge 契约测试和前端构建验证。

## 测试计划

### Core/Application

- Active Skill 快照只允许已激活 Skill。
- 未激活 Skill 的脚本和资源调用被拒绝。
- 显式选择、触发词推荐、排除词、模式过滤和标签关闭行为正确。
- Prompt 预算、章节截断、安全边界保留和资源按需加载正确。
- 推荐和 Skill 观测失败不会中断 Agent 主流程。

### Infrastructure

- manifest 兼容解析、旧 Skill 默认值、非法信任级别和工具依赖校验。
- 原子保存、并发修改检测、历史版本恢复和损坏状态恢复。
- 授权键在脚本/来源/能力/路径变化后正确失效。
- 外部 Skill 默认禁用，显式切换后才允许进入确认流程。

### SkillHost/OfficeIntegration

- 正常 C#、Python 请求的 JSON 回环。
- 脚本超时、取消、异常退出、子进程退出和进程树清理。
- 输入总量、输出总量、输出文件数量、脚本大小和 stdout/stderr 上限。
- workspace TTL 清理、路径越界、符号链接/Junction 拒绝。
- `read_skill_resource` 只能读取 Active Skill 声明资源。
- 宿主崩溃不会影响测试进程和 Word 主流程。

### AddIn/Web

- Bridge JSON 契约、`skill_recommendation` 事件映射和脚本策略接口。
- 前端 Active Skill 标签显示、关闭和任务结束清理。
- 280px 宽度下的推荐、信任状态、资源和观测摘要不溢出。
- 前端生产构建和现有 Skill 管理回归。

## 风险与注意事项

- 独立进程降低 AddIn 崩溃风险，但在未使用 AppContainer/WASM 前不能宣称是强沙箱；UI 和文档必须明确这一点。
- Job Object 和标准输入/输出会增加每次脚本调用的启动延迟，目标是增加不超过 500ms；超出时记录观测。
- 自动推荐可能误激活 Skill，必须显示标签并允许当前任务关闭；低置信度不自动激活。
- 本地观测可能包含敏感错误片段，必须继续使用现有脱敏逻辑并设置摘要长度上限。
- 资源和脚本配额过低会导致合法批处理失败，超限错误必须说明实际限制并允许用户对可信 Skill 逐次确认放宽。
- Skill 内容哈希和脚本哈希变化会使旧授权失效，前端必须给出可理解的“脚本已更新，需要重新确认”提示。
- 任何新安全校验失败都应返回结构化 ToolCallResult，不得抛出未处理异常破坏 Agent 主循环。

## 验收标准

- 脚本超时或取消不会持续占用 Word AddIn 进程，宿主及其子进程可被回收。
- 未激活、未启用或外部默认禁用 Skill 的脚本/资源调用均被阻止。
- 用户可以看见本次实际使用的 Skill，并能关闭自动推荐而不改变全局设置。
- Skill Prompt 在默认预算内加载，资源按需读取，不再无条件加载全部资源内容。
- 观测能够回答“哪个 Skill 在哪个模型/模式下被使用、是否成功、消耗多少 token、是否触发脚本”，且不需要上传文档正文。
- 现有 Skill 管理、脚本授权、Agent 工具调用、Word 写入验证和已有测试全部保持通过。

