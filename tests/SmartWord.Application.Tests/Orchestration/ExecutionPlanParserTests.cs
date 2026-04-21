using SmartWord.Application.Orchestration;
using SmartWord.Core.Models;
using Xunit;

namespace SmartWord.Application.Tests.Orchestration
{
    public class ExecutionPlanParserTests
    {
        [Fact]
        public void TryParse_FencedJsonBlock_ReturnsExecutionPlan()
        {
            var content = @"```json
{
  ""task_description"": ""统一文档标题格式"",
  ""todo_list"": [""扫描所有标题"", ""统一一级标题样式"", ""校验目录是否同步""],
  ""risk_notes"": [""部分标题可能处于表格中""]
}
```";

            var success = ExecutionPlanParser.TryParse(content, out var plan);

            Assert.True(success);
            Assert.NotNull(plan);
            Assert.Equal("统一文档标题格式", plan.TaskDescription);
            Assert.Equal(3, plan.TodoList.Count);
            Assert.Equal("统一一级标题样式", plan.TodoList[1].Description);
            Assert.Single(plan.RiskNotes);
        }

        [Fact]
        public void TryParse_PlainJsonWithCamelCaseAndObjectTodoItems_ReturnsExecutionPlan()
        {
            var content = @"{
  ""taskDescription"": ""润色摘要并保留原始结构"",
  ""todoList"": [
    { ""description"": ""读取摘要段落"", ""status"": ""completed"" },
    { ""content"": ""润色表达并统一术语"", ""status"": ""in_progress"" },
    { ""title"": ""回读核对格式"" }
  ],
  ""riskNotes"": [
    { ""content"": ""不要改动表格中的术语"" }
  ]
}";

            var success = ExecutionPlanParser.TryParse(content, out var plan);

            Assert.True(success);
            Assert.NotNull(plan);
            Assert.Equal("润色摘要并保留原始结构", plan.TaskDescription);
            Assert.Equal(3, plan.TodoList.Count);
            Assert.Equal(TodoItemStatus.Completed, plan.TodoList[0].Status);
            Assert.Equal(TodoItemStatus.InProgress, plan.TodoList[1].Status);
            Assert.Equal("回读核对格式", plan.TodoList[2].Description);
            Assert.Single(plan.RiskNotes);
            Assert.Equal("不要改动表格中的术语", plan.RiskNotes[0]);
        }

        [Fact]
        public void TryParse_TextWrappedJson_ReturnsExecutionPlan()
        {
            var content = @"下面是规划结果，请确认：

{
  ""task_description"": ""修正引文格式"",
  ""todo_list"": [""定位所有引文"", ""统一引文括号格式"", ""抽查结果""],
  ""risk_notes"": []
}

如果确认无误即可开始。";

            var success = ExecutionPlanParser.TryParse(content, out var plan);

            Assert.True(success);
            Assert.NotNull(plan);
            Assert.Equal(3, plan.TodoList.Count);
            Assert.Equal("统一引文括号格式", plan.TodoList[1].Description);
        }

        [Fact]
        public void TryParse_MarkdownPlan_ReturnsExecutionPlan()
        {
            var content = @"# 执行蓝图

## 任务说明
保留原结构，仅统一摘要和结论部分的语言风格。

## 待办清单
- 检查摘要与结论段落
- 统一术语与语气
- 回读确认没有误改

## 风险提示
- 注意不要修改表格内容
- 注意保留原始数据数值";

            var success = ExecutionPlanParser.TryParse(content, out var plan);

            Assert.True(success);
            Assert.NotNull(plan);
            Assert.Equal("保留原结构，仅统一摘要和结论部分的语言风格。", plan.TaskDescription);
            Assert.Equal(3, plan.TodoList.Count);
            Assert.Equal("统一术语与语气", plan.TodoList[1].Description);
            Assert.Equal(2, plan.RiskNotes.Count);
        }

        [Fact]
        public void TryParse_JsonWithoutTodoList_ReturnsFalse()
        {
            var content = @"{ ""task_description"": ""只有说明，没有步骤"" }";

            var success = ExecutionPlanParser.TryParse(content, out var plan);

            Assert.False(success);
            Assert.Null(plan);
        }
    }
}
