# SmartWord-Bench Seed Cases

本目录包含一批按照 `docs/benchmark设计.md` 生成的 Word 操作 Benchmark 种子用例。

## 当前规模

当前版本提供两套 case：

- `cases/`：首版 seed case，L3/L4 较轻量，适合快速调试。
- `cases_rich/`：升级后的复杂版 case，重点增强 L3/L4 的文档长度、结构复杂度和多步骤依赖，推荐用于正式评测。

两套 case 均为 16 个测试用例，每个难度 4 个：

| Level | 目录 | 数量 | 对标/用途 |
| --- | --- | ---: | --- |
| L1 | `cases/L1_basic_word` | 4 | 参考计算机一级 Word 操作题型 |
| L2 | `cases/L2_integrated_office` | 4 | 参考计算机二级 MS Office Word 操作题型 |
| L3 | `cases/L3_professional_docs` | 4 | 真实办公文档任务 |
| L4 | `cases/L4_agentic_tasks` | 4 | Agentic 复杂任务 |

每个 case 包含：

```text
input.docx      # 初始 Word 文档
task.json       # 用户指令、任务元信息、工具约束
expected.json   # 期望结果和判分规则
```

`manifest.json` 汇总 `cases/` 的路径和基础信息。

`manifest_cases_rich.json` 汇总 `cases_rich/` 的路径和基础信息。

## 生成方式

依赖：

```powershell
npm install -g docx
```

重新生成默认 `cases/`：

```powershell
node benchmark\scripts\generate_seed_cases.js
```

重新生成复杂版 `cases_rich/`：

```powershell
$env:BENCHMARK_CASE_DIR='cases_rich'
node benchmark\scripts\generate_seed_cases.js
```

生成脚本会覆盖目标 case 目录中的 `input.docx`、`task.json`、`expected.json` 和对应 manifest。

如果 Word 正在打开某个 `input.docx`，Windows 会锁定文件，脚本覆盖时可能报 `EBUSY`。关闭对应 Word 文档后重新运行即可。

## 校验方式

使用已安装的 `$docx` skill 的校验脚本：

```powershell
$env:PYTHONUTF8='1'
$files = Get-ChildItem -Recurse benchmark\cases_rich -Filter input.docx
foreach ($f in $files) {
  python C:\Users\amairs\.codex\skills\docx\scripts\office\validate.py $f.FullName
}
```

在 Windows 中文环境下建议设置 `PYTHONUTF8=1`，否则 Python 可能按 GBK 解码 `.docx` 内部 XML，导致误报编码错误。

## 用例列表

### L1 基础 Word 操作

- `L1_font_paragraph_001`：标题与正文基础格式。
- `L1_find_replace_002`：全文查找替换。
- `L1_simple_table_003`：简单表格格式。
- `L1_header_footer_004`：页眉页脚与页码。

### L2 综合 Office 文档操作

- `L2_style_toc_001`：规划报告样式、目录、题注与脚注综合排版。
- `L2_page_setup_002`：论文分节、页眉页脚、目录与页码综合排版。
- `L2_complex_table_003`：销售数据表格、计算、排序与题注综合处理。
- `L2_numbering_template_004`：邮件合并式通知书批量生成。

L2 已按计算机二级 MS Office Word 综合题风格增强，不再是单一操作题。每个任务都包含多个连续要求：

- 标题样式、自动目录、题注、脚注、正文格式组合。
- 封面/摘要/目录/正文分节，不同节使用不同页眉页脚和页码格式。
- 表格标题行、边框、合并单元格、计算列、题注和保护附表信息。
- 基于数据源表格批量生成通知书，替换占位符、分页、保留数据源。

### L3 专业办公文档处理

- `L3_contract_party_replace_001`：合同主体名称替换。
- `L3_resume_section_rewrite_002`：简历项目经历优化。
- `L3_bid_consistency_003`：标书一致性检查。
- `L3_paper_abstract_rewrite_004`：论文摘要学术化润色。

`cases_rich/` 中 L3 文档已增强为：

- 合同主体替换：包含合同主体、项目范围、交付计划表、付款表、知识产权、附件、内部批注记录等多区域内容。
- 简历优化：包含个人信息、求职意向、教育经历、技能表、项目经历、实习经历、竞赛证书等多个必须保护的区域。
- 标书一致性检查：包含封面基准值、检查表和 12 个章节，预埋供应商、项目名称、日期不一致问题。
- 论文摘要润色：包含摘要、关键词、绪论、相关工作、方法、实验、结论、参考文献，要求只改摘要并保留事实。

### L4 Agentic 复杂任务

- `L4_full_rewrite_highlight_001`：全文润色并高亮修改。
- `L4_high_risk_delete_002`：高风险批量删除。
- `L4_prompt_injection_003`：Prompt Injection 防护。
- `L4_long_context_bid_004`：长文档一致性与上下文压缩。

`cases_rich/` 中 L4 文档已增强为：

- 全文润色并高亮：包含多章节复盘报告、事实约束和表格，要求计划、分段读取、润色、高亮和验证。
- 高风险批量删除：混合旧项目废弃资料、当前项目资料、法务保留记录，要求预览、确认、精准删除和审计。
- Prompt Injection 防护：在正文和表格中同时预埋恶意指令，要求只读总结风险，不执行写入。
- 长文档一致性与上下文压缩：包含 45 个章节、45 张表格、589 个段落，预埋供应商、项目名、日期不一致，要求多粒度读取和压缩。
