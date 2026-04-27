# P1-3 本地 SQLite 持久化与任务历史审计

## 背景

SmartWord 当前对话历史主要依赖内存存储，Word 重启后无法继续读取当前文档的历史上下文；Agent 执行过程虽然会向前端实时发送工具、改动、验证、暂停等事件，但缺少一个可长期追溯的任务历史中心。

## 用户目标

- 重启 Word 后仍可读取当前文档对话历史。
- 当前文档可查看最近任务，知道每次 Ask / Plan / Agent 做了什么。
- Agent 写入任务可审计工具调用、文档改动、验证结果和失败/取消/暂停原因。
- 历史面板可跳转到相关段落确认结果。

## 非目标

- 不迁移 Todo Board JSON 运行态。
- 不实现全文搜索、历史导出、历史删除、数据库加密和跨设备同步。
- 不支持从历史重新运行任务或继续历史中的暂停任务。

## 数据与隐私

数据库路径固定为 `%AppData%\SmartWord\smartword.db`。首版完整保存用户消息、助手消息、工具输入输出和任务改动摘要，但所有写入入口会执行最小密钥脱敏，避免明显 API Key、Bearer Token、Authorization 头和设置密钥字段落库。

## 技术范围

- Core 新增 `ITaskHistoryStore` 和任务历史模型。
- Infrastructure 新增 SQLite 数据库初始化、`SqliteConversationStore`、`SqliteTaskHistoryStore`。
- Application 在 `AgentOrchestrator` 中旁路记录任务开始、工具结果、文档改动和最终状态。
- AddIn DI 切换为 SQLite 对话存储，并暴露历史查询 bridge。
- Vue 前端新增历史按钮、历史 store 和任务历史面板。
