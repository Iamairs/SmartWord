using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SmartWord.EvalRunner
{
    /// <summary>基于 OpenXML 快照执行表格结构与内容检查。</summary>
    internal sealed class TableCheckScorer : CheckScorerBase
    {
        public TableCheckScorer()
            : base(
                "table_header_style", "table_borders", "no_unexpected_table_change",
                "calculated_cells", "merged_cells", "table_vertical_alignment",
                "table_content_preserved", "table_cells_replaced", "data_source_table_preserved",
                "table_header_shading_and_border", "table_repeating_header", "unchanged_table_content")
        {
        }

        public override CheckResult Score(ScoreContext context)
        {
            switch (context.Check.Value<string>("type") ?? string.Empty)
            {
                case "table_header_style": return HeaderStyle(context);
                case "table_borders": return Borders(context);
                case "no_unexpected_table_change": return NoUnexpectedTableChange(context);
                case "calculated_cells": return CalculatedCells(context);
                case "merged_cells": return MergedCells(context);
                case "table_vertical_alignment": return VerticalAlignment(context);
                case "table_content_preserved": return PreservedColumns(context);
                case "table_cells_replaced": return ReplacedCells(context);
                case "data_source_table_preserved": return DataSourcePreserved(context);
                case "table_header_shading_and_border": return HeaderShadingAndBorder(context);
                case "table_repeating_header": return RepeatingHeader(context);
                case "unchanged_table_content": return UnchangedTableContent(context);
                default: return CheckResult.Unsupported(context.Check.Value<string>("type") ?? string.Empty, Points(context.Check), "表格 scorer 未识别该检查类型。");
            }
        }

        private static CheckResult HeaderStyle(ScoreContext c)
        {
            var table = GetTable(c, out var reason);
            var rowIndex = c.Check.Value<int?>("row_index") ?? 1;
            if (table == null) return CheckResult.Unsupported(Type(c), Points(c.Check), reason);
            var row = table.Rows.ElementAtOrDefault(Math.Max(0, rowIndex - 1));
            if (row == null) return CheckResult.Unsupported(Type(c), Points(c.Check), "指定表头行不存在。");
            var boldExpected = c.Check.Value<bool?>("bold");
            var alignmentExpected = c.Check.Value<string>("alignment");
            var boldActual = row.Cells.All(cell => cell.Paragraphs.Any(p => p.Bold));
            var alignmentActual = row.Cells.SelectMany(cell => cell.Paragraphs).Select(p => p.Alignment).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
            var passed = (!boldExpected.HasValue || boldActual == boldExpected.Value)
                && (string.IsNullOrWhiteSpace(alignmentExpected) || string.Equals(alignmentActual, alignmentExpected, StringComparison.OrdinalIgnoreCase));
            return Result(c, "table", passed, passed ? "表头行样式符合要求。" : "表头行样式不符合要求。", c.Check.ToString(Newtonsoft.Json.Formatting.None), "bold=" + boldActual + ", alignment=" + alignmentActual);
        }

        private static CheckResult Borders(ScoreContext c)
        {
            var table = GetTable(c, out var reason);
            if (table == null) return CheckResult.Unsupported(Type(c), Points(c.Check), reason);
            var passed = table.HasBorders;
            return Result(c, "table", passed, passed ? "表格包含边框定义。" : "表格未找到边框定义。", c.Check.Value<string>("border_style") ?? "有边框", passed ? "已定义" : "未定义");
        }

        private static CheckResult NoUnexpectedTableChange(ScoreContext c)
        {
            var index = c.Check.Value<int?>("table_index");
            if (!index.HasValue) return CheckResult.Unsupported(Type(c), Points(c.Check), "缺少 table_index。");
            var input = c.Input.Tables.ElementAtOrDefault(index.Value - 1);
            var output = c.Output.Tables.ElementAtOrDefault(index.Value - 1);
            if (input == null || output == null) return Result(c, "table", false, "目标表格不存在。");
            var passed = c.Input.Tables.Count == c.Output.Tables.Count && DocxSnapshot.Normalize(input.Text) == DocxSnapshot.Normalize(output.Text);
            return Result(c, "table", passed, passed ? "未发现非预期表格变化。" : "表格数量或内容发生非预期变化。", "表格保持不变", passed ? "未变化" : "已变化");
        }

        private static CheckResult CalculatedCells(ScoreContext c)
        {
            var table = GetTable(c, out var reason);
            var values = ReadStrings(c.Check["values"]);
            var column = ReadInt(c.Check["column"]) ?? ParseColumn(c.Check.Value<string>("column"));
            if (table == null) return CheckResult.Unsupported(Type(c), Points(c.Check), reason);
            if (!column.HasValue) column = ResolveColumn(table, c.Check.Value<string>("column"));
            if (!column.HasValue || values.Count == 0) return CheckResult.Unsupported(Type(c), Points(c.Check), "缺少 calculated_cells 的 column 或 values。");
            var actual = table.Rows.Skip(1).Select(row => row.Cells.ElementAtOrDefault(column.Value - 1)?.Text ?? string.Empty).ToList();
            var passed = values.Count == actual.Count && values.Zip(actual, (expected, actualValue) => DocxSnapshot.Normalize(expected) == DocxSnapshot.Normalize(actualValue)).All(item => item);
            return Result(c, "table", passed, passed ? "计算列值全部匹配。" : "计算列值与期望不一致。", string.Join("|", values), string.Join("|", actual));
        }

        private static CheckResult MergedCells(ScoreContext c)
        {
            var table = GetTable(c, out var reason);
            var groups = c.Check["groups"] as JArray;
            var column = c.Check.Value<int?>("column_index");
            if (table == null) return CheckResult.Unsupported(Type(c), Points(c.Check), reason);
            if (!column.HasValue && groups == null) return CheckResult.Unsupported(Type(c), Points(c.Check), "缺少合并单元格定位字段。");
            var actual = table.Rows.Select(row => row.Cells.ElementAtOrDefault((column ?? 1) - 1)).Count(cell => cell != null && (cell.HasVerticalMerge || cell.GridSpan > 1));
            var expected = groups?.Count ?? 1;
            var passed = actual >= expected;
            return Result(c, "table", passed, passed ? "合并单元格结构符合要求。" : "未找到足够的合并单元格。", expected.ToString(), actual.ToString());
        }

        private static CheckResult VerticalAlignment(ScoreContext c)
        {
            var table = GetTable(c, out var reason);
            var expected = c.Check.Value<string>("alignment");
            if (table == null) return CheckResult.Unsupported(Type(c), Points(c.Check), reason);
            if (string.IsNullOrWhiteSpace(expected)) return CheckResult.Unsupported(Type(c), Points(c.Check), "缺少 alignment。");
            var actual = table.Rows.SelectMany(row => row.Cells).Select(cell => cell.VerticalAlignment).ToList();
            var passed = actual.Count > 0 && actual.All(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
            return Result(c, "table", passed, passed ? "单元格垂直对齐符合要求。" : "单元格垂直对齐不一致。", expected, string.Join(",", actual.Distinct()));
        }

        private static CheckResult PreservedColumns(ScoreContext c)
        {
            var index = c.Check.Value<int?>("table_index");
            var columns = c.Check["protected_columns"] as JArray;
            if (!index.HasValue || columns == null) return CheckResult.Unsupported(Type(c), Points(c.Check), "缺少 table_index 或 protected_columns。");
            var input = c.Input.Tables.ElementAtOrDefault(index.Value - 1);
            var output = c.Output.Tables.ElementAtOrDefault(index.Value - 1);
            if (input == null || output == null) return Result(c, "table", false, "目标表格不存在。");
            var failed = new List<string>();
            foreach (var token in columns)
            {
                var column = ReadInt(token) ?? ResolveColumn(input, token.Value<string>());
                if (!column.HasValue) continue;
                var left = input.Rows.Select(row => row.Cells.ElementAtOrDefault(column.Value - 1)?.Text ?? string.Empty);
                var right = output.Rows.Select(row => row.Cells.ElementAtOrDefault(column.Value - 1)?.Text ?? string.Empty);
                if (!left.Zip(right, (a, b) => DocxSnapshot.Normalize(a) == DocxSnapshot.Normalize(b)).All(item => item)) failed.Add(column.Value.ToString());
            }
            return Result(c, "table", failed.Count == 0, failed.Count == 0 ? "受保护列内容均保留。" : "受保护列发生变化：" + string.Join(",", failed), "受保护列不变", failed.Count == 0 ? "未变化" : string.Join(",", failed));
        }

        private static CheckResult ReplacedCells(ScoreContext c)
        {
            var tables = c.Check["tables"] as JArray;
            var from = c.Check.Value<string>("from");
            var to = c.Check.Value<string>("to");
            if (tables == null || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return CheckResult.Unsupported(Type(c), Points(c.Check), "缺少 tables/from/to。");
            var failed = new List<string>();
            foreach (var token in tables)
            {
                var index = ReadInt(token) ?? ParseColumn(token.Value<string>());
                var table = index.HasValue ? c.Output.Tables.ElementAtOrDefault(index.Value - 1) : null;
                if (table == null || table.Text.Contains(from) || !table.Text.Contains(to)) failed.Add(token.ToString());
            }
            return Result(c, "table", failed.Count == 0, failed.Count == 0 ? "指定表格单元格均已替换。" : "未完成替换的表格：" + string.Join(",", failed), to, failed.Count == 0 ? "全部完成" : string.Join(",", failed));
        }

        private static CheckResult DataSourcePreserved(ScoreContext c)
        {
            var passed = c.Input.Tables.Count == c.Output.Tables.Count && c.Input.Tables.Zip(c.Output.Tables, (a, b) => DocxSnapshot.Normalize(a.Text) == DocxSnapshot.Normalize(b.Text)).All(item => item);
            return Result(c, "table", passed, passed ? "数据源表内容保持不变。" : "数据源表内容发生变化。", "全部数据源表不变", passed ? "未变化" : "已变化");
        }

        private static CheckResult HeaderShadingAndBorder(ScoreContext c)
        {
            var table = GetTable(c, out var reason);
            if (table == null) return CheckResult.Unsupported(Type(c), Points(c.Check), reason);
            var passed = table.HasBorders && table.HasHeaderShading;
            return Result(c, "table", passed, passed ? "表头底纹和边框均存在。" : "表头底纹或边框缺失。", "底纹+边框", "底纹=" + table.HasHeaderShading + ", 边框=" + table.HasBorders);
        }

        private static CheckResult RepeatingHeader(ScoreContext c)
        {
            var table = GetTable(c, out var reason);
            var row = c.Check.Value<int?>("row_index") ?? 1;
            if (table == null) return CheckResult.Unsupported(Type(c), Points(c.Check), reason);
            var actual = table.Rows.ElementAtOrDefault(row - 1)?.IsRepeatingHeader == true;
            return Result(c, "table", actual, actual ? "表头行已设置跨页重复。" : "表头行未设置跨页重复。", "重复标题行", actual ? "已设置" : "未设置");
        }

        private static CheckResult UnchangedTableContent(ScoreContext c)
        {
            var passed = c.Input.Tables.Count == c.Output.Tables.Count && c.Input.Tables.Zip(c.Output.Tables, (a, b) => DocxSnapshot.Normalize(a.Text) == DocxSnapshot.Normalize(b.Text)).All(item => item);
            return Result(c, "table", passed, passed ? "所有表格内容保持不变。" : "表格内容发生变化。", "表格内容不变", passed ? "未变化" : "已变化");
        }

        private static DocxTable GetTable(ScoreContext c, out string reason)
        {
            var index = c.Check.Value<int?>("table_index") ?? 1;
            var table = c.Output.Tables.ElementAtOrDefault(index - 1);
            reason = table == null ? "指定表格不存在。" : string.Empty;
            return table;
        }

        private static int? ParseColumn(string value)
        {
            return int.TryParse(value, out var number) ? number : (int?)null;
        }

        private static int? ResolveColumn(DocxTable table, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var header = table.Rows.FirstOrDefault();
            if (header == null) return null;
            for (var i = 0; i < header.Cells.Count; i++)
            {
                if (DocxSnapshot.ContainsNormalized(header.Cells[i].Text, name)) return i + 1;
            }
            return null;
        }

        private static int? ReadInt(JToken token)
        {
            if (token == null) return null;
            return token.Type == JTokenType.Integer ? token.Value<int>() : ParseColumn(token.Value<string>());
        }

        private static string Type(ScoreContext c) => c.Check.Value<string>("type") ?? string.Empty;
    }
}
