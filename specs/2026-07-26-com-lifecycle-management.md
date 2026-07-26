# 强化 COM 生命周期管理

## 需求背景

WordApplicationWrapper 已集中处理 UI 线程和部分 COM 释放，但仍存在 dynamic、人工释放、关闭竞态和 Word Busy 风险。

## 目标

统一 COM 所有权与释放；强化 UI 调度关闭语义；只读操作有限重试；修复 EvalRunner 生命周期。

## 修改范围

OfficeIntegration 的生命周期基础设施、包装器与相关工具；EvalRunner；对应单元测试。

## 不在范围

不移除脚本 dynamic；不改工具/Bridge/Undo 契约；不释放或退出宿主 Application；不要求真实 Word 集成测试。

## 实现方案

新增 ComScope 与释放助手；新增关闭安全的调度器；增加只读 Busy 重试；迁移现有人工释放；修复 EvalRunner 子对象释放。

## 测试计划

覆盖逆序释放、幂等清理、调度关闭、Busy 重试与非瞬时错误，并运行匹配范围的自动化测试。

## 风险与注意事项

只释放本地明确拥有的 RCW；写操作不自动重试；真实 Word 行为保留后续人工验证。
