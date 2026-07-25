# 移除旧版临时规划文档要求

## 需求背景

当前仓库已经改为 Spec 优先开发流程：任何开发或代码修改前，需要先从主分支拉出规范命名的新分支，并在 `specs/` 目录中新建本次任务的 spec 文档。

`AGENTS.md` 中仍保留复杂功能需要编写 `docs/project_cur.md` 与 `docs/plan_cur.md` 的旧流程说明，会与新的 Spec 优先流程产生重复和歧义。

## 目标

- 删除 `AGENTS.md` 中关于 `docs/project_cur.md` 和 `docs/plan_cur.md` 的要求。
- 保留并强化当前 `specs/` 目录下的 Spec 优先流程。
- 保持开发规范简洁一致，避免同时维护两套规划文档。

## 修改范围

- `AGENTS.md` 的开发规范章节。
- 新增本 spec 文档记录本次规范调整。

## 不在范围

- 不修改项目代码。
- 不修改现有 `docs/project_cur.md`、`docs/plan_cur.md` 文件（如果存在）。
- 不改动测试代码或构建脚本。

## 实现方案

- 删除复杂功能旧流程中要求编写 `docs/project_cur.md` 和 `docs/plan_cur.md` 的 step 描述。
- 用统一说明替代：简单和复杂任务都应遵循分支与 Spec 优先流程；复杂功能可以在 spec 中写更详细的背景、方案、任务拆分、测试计划和风险。

## 测试计划

- 运行 `git diff --check`，确认 Markdown 修改没有尾随空格或补丁格式问题。
- 通过 UTF-8 读取 `AGENTS.md`，确认旧文件名要求已删除。

## 风险与注意事项

- 本次只调整开发规范文档，不涉及产品运行逻辑。
- 需要避免把工作区中既有的 benchmark 修改纳入本次提交。
