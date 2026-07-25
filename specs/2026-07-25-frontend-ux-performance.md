# 前端风格、交互体验与卡顿优化

## 需求背景

SmartWord 前端运行在 Word VSTO 侧边栏中，实际宽度通常约 280-320px。当前界面已经具备聊天、常用任务、设置、Skill、任务历史、工具调用追踪、写入确认和任务板能力，但存在以下可优化点：

- 顶部操作区在窄宽度下信息密度偏高，标题、设置摘要和功能按钮争抢空间。
- 运行模式入口藏在高级选项内，Ask / Plan / Agent 的切换不够直接。
- 常用任务面板始终占用较多高度，长对话时压缩消息区域。
- 流式输出期间每个 chunk 都可能触发响应式更新、Markdown 渲染和滚动计算，容易造成 WebView2 侧边栏卡顿。
- 消息 Markdown 通过模板方法直接渲染，频繁重渲染时会重复解析历史消息。
- 多个卡片视觉风格接近但不完全统一，状态反馈和焦点可见性还可以更清晰。

## 参考原则

- Fluent 2：强调内容层级、清晰布局、熟悉且克制的动效，适合贴近 Microsoft Office 的插件体验。
- Apple HIG：动效应服务于状态反馈和上下文理解，避免无意义的装饰动画。
- Material 3：通过表面层级、状态色、motion token 和可访问焦点来提升可感知反馈。

## 目标

- 让 280px 侧边栏下的主路径更直接：模式选择、输入、发送、取消、确认写入都能快速定位。
- 让界面更接近现代办公 AI 工具：轻量表面、清晰层级、紧凑卡片、明确状态色。
- 减少流式输出导致的卡顿：批量合并 chunk、缓存 Markdown 渲染结果、优化自动滚动时机。
- 保留现有功能与事件协议，不改 C# bridge 和后端接口。
- 前端构建通过。

## 修改范围

- `web/SmartWord.WebClient/src/main.js`
- `web/SmartWord.WebClient/src/styles/theme.css`
- `web/SmartWord.WebClient/src/components/ChatWindow.vue`
- `web/SmartWord.WebClient/src/components/QuickActionsPanel.vue`
- `web/SmartWord.WebClient/src/components/ThoughtActionTrace.vue`
- `web/SmartWord.WebClient/src/components/ContentPreviewPanel.vue`

## 不在范围

- 不修改 C# 后端、Agent 编排、LLM 协议和 Word COM 工具。
- 不新增前端依赖，避免引入体积和兼容性风险。
- 不重做完整设计系统，只做本次高收益的局部统一和性能修复。
- 不处理现有 benchmark 目录中的既有未提交修改。

## 实现方案

1. 新增全局主题 token：
   - 使用 CSS 变量定义颜色、间距、圆角、阴影、动效时长和焦点样式。
   - 增加 `prefers-reduced-motion` 兼容，减少系统动效敏感用户负担。
2. 优化 ChatWindow：
   - 顶部改为紧凑单列结构，品牌、当前模式和操作按钮层级更清晰。
   - 将 Ask / Plan / Agent 模式选择从高级选项上移到输入区显性位置。
   - 高级选项仅保留 Skill 和环境信息，降低主路径干扰。
   - 增加输入字数计数和更明确的模式提示。
3. 优化性能：
   - 流式 `stream_chunk` 事件使用 requestAnimationFrame 批量 flush，减少高频响应式更新。
   - Markdown 渲染结果按消息 id、内容和 citation 版本缓存，避免历史消息重复解析。
   - 自动滚动改为 requestAnimationFrame 调度，并只在接近底部时跟随，避免用户回看历史时被强制拉回。
   - 对消息卡片启用 `content-visibility` / `contain` 等浏览器渲染提示，降低长对话渲染成本。
4. 优化关键面板：
   - QuickActions 改为可折叠面板，默认展示但可收起。
   - Tool trace 与确认面板统一使用主题 token，状态点、风险标签和按钮层级更明确。
   - 增强 hover / focus-visible / disabled 状态。

## 测试计划

- 在 `web/SmartWord.WebClient` 下运行 `npm run build`。
- 在仓库根目录运行 `git diff --check`。
- 检查工作区状态，确保仅提交本次 spec 和前端相关文件，不混入 benchmark 修改。
- 如构建产物同步到 AddIn Resources，纳入同一提交。

## 风险与注意事项

- WebView2 版本差异可能影响 `content-visibility`，该属性为渐进增强，不影响基础功能。
- Markdown 缓存需要在 citation 变化时失效，否则引用按钮可能不刷新，因此缓存 key 包含 citation 版本。
- 流式 chunk 批量 flush 会把多个小片段合并到一帧内显示，视觉上更顺滑，但不会改变最终内容。
