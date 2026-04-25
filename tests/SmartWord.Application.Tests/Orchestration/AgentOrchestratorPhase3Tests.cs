using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWord.Application.Todo;
using SmartWord.Application.Orchestration;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Tools;
using SmartWord.Application.Context;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.Persistence;
using Xunit;

namespace SmartWord.Application.Tests.Orchestration
{
    public class AgentOrchestratorPhase3Tests
    {
        [Fact]
        public async Task RunAsync_AgentModeDocumentNotWritable_StopsBeforeCallingModel()
        {
            var orchestrator = CreateOrchestrator(
                new FakeLlmClient(),
                new FakeContextHydrator(new DocumentContext
                {
                    DocumentPath = "doc1",
                    DocumentStatus = new DocumentStatus
                    {
                        IsWritable = false,
                        IsReadOnly = true
                    }
                }));

            var events = await CollectAsync(orchestrator.RunAsync(
                "请帮我改标题",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true
                },
                CancellationToken.None));

            Assert.Single(events);
            Assert.Equal(AgentEventType.DocumentNotWritable, events[0].Type);
        }

        [Fact]
        public async Task RunAsync_ModelCallFailsBeforeAnyWrite_DoesNotCreateUndoScope()
        {
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                new ThrowingLlmClient(new TimeoutException("等待 LLM 响应超时，超过 120 秒。")),
                CreateWritableHydrator(),
                undoScopeFactory: undoScopeFactory);

            await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                await CollectAsync(orchestrator.RunAsync(
                    "请修改标题格式",
                    new AgentRunOptions
                    {
                        Mode = AgentMode.Agent,
                        EnableToolCalling = true
                    },
                    CancellationToken.None));
            });

            Assert.Null(undoScopeFactory.LastScope);
        }

        [Fact]
        public async Task RunAsync_PlanModeQuestionWithoutId_GeneratesStableIdAndSkipsRemainingToolCalls()
        {
            var conversationStore = new InMemoryConversationStore();
            var questionChannel = new FakeQuestionChannel("保留原结构");
            var orchestrator = CreateOrchestrator(
                new FakeLlmClient(
                    CreateToolCallMessage(
                        CreateToolCall(
                            string.Empty,
                            "ask_user_question",
                            "{\"question\":\"是否保留当前结构？\",\"options\":[\"保留原结构\",\"重新组织\"]}"),
                        CreateToolCall(
                            "plan-q-2",
                            "ask_user_question",
                            "{\"question\":\"语气偏正式还是简洁？\",\"options\":[\"正式\",\"简洁\"]}")),
                    new AgentMessage
                    {
                        Role = "assistant",
                        Content = "```json\n{\"task_description\":\"保留当前结构并优化表达。\",\"todo_list\":[\"梳理现有结构\",\"统一语言风格\"],\"risk_notes\":[\"第二个采访问题已延后到下一轮处理。\"]}\n```"
                    }),
                CreateWritableHydrator(),
                new ITool[]
                {
                    new FakeTool("ask_user_question", ToolPermission.ReadOnly, ToolCallResult.Ok("{}"))
                },
                conversationStore: conversationStore,
                questionChannel: questionChannel);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请先帮我规划怎么润色这篇文档",
                new AgentRunOptions
                {
                    Mode = AgentMode.Plan,
                    EnableToolCalling = true
                },
                CancellationToken.None));

            var questionEvent = Assert.Single(events.Where(item => item.Type == AgentEventType.QuestionAsked));
            Assert.False(string.IsNullOrWhiteSpace(questionEvent.ToolCallId));
            Assert.Equal(questionEvent.ToolCallId, questionChannel.LastQuestionId);
            Assert.Contains(events, item => item.Type == AgentEventType.PlanReady);

            var history = await conversationStore.GetHistoryAsync("doc1", CancellationToken.None);
            var toolMessages = history.Where(item => string.Equals(item.Role, "tool", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.Equal(2, toolMessages.Count);
            Assert.Equal(questionEvent.ToolCallId, toolMessages[0].ToolCallId);
            Assert.Equal("plan-q-2", toolMessages[1].ToolCallId);
            Assert.Contains("剩余工具调用已跳过", toolMessages[1].Content);
        }

        [Fact]
        public async Task RunAsync_PlanModePlainJsonPlan_EmitsPlanReady()
        {
            var orchestrator = CreateOrchestrator(
                new FakeLlmClient(
                    new AgentMessage
                    {
                        Role = "assistant",
                        Content = "{\n"
                            + "  \"taskDescription\": \"统一段落间距并保留正文结构\",\n"
                            + "  \"todoList\": [\n"
                            + "    { \"description\": \"扫描正文段落\" },\n"
                            + "    { \"content\": \"统一段前段后间距\", \"status\": \"in_progress\" },\n"
                            + "    { \"title\": \"抽查关键页面\" }\n"
                            + "  ],\n"
                            + "  \"riskNotes\": [\"表格内段落不应受影响\"]\n"
                            + "}"
                    }),
                CreateWritableHydrator(),
                new ITool[]
                {
                    new FakeTool("probe_document", ToolPermission.ReadOnly, ToolCallResult.Ok("{\"ok\":true}"))
                });

            var events = await CollectAsync(orchestrator.RunAsync(
                "先规划如何统一段落间距",
                new AgentRunOptions
                {
                    Mode = AgentMode.Plan,
                    EnableToolCalling = true
                },
                CancellationToken.None));

            var planReadyEvent = Assert.Single(events.Where(item => item.Type == AgentEventType.PlanReady));
            Assert.Contains("\"taskDescription\":\"统一段落间距并保留正文结构\"", planReadyEvent.PlanJson);
            Assert.Contains("\"description\":\"统一段前段后间距\"", planReadyEvent.PlanJson);
        }

        [Fact]
        public async Task RunAsync_AgentModeWithActivePlan_EmitsTodoBoardReady()
        {
            var orchestrator = CreateOrchestrator(
                new FakeLlmClient(new AgentMessage
                {
                    Role = "assistant",
                    Content = "任务已执行完成。"
                }),
                CreateWritableHydrator());

            var events = await CollectAsync(orchestrator.RunAsync(
                "请执行当前计划",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    ActivePlan = new ExecutionPlan
                    {
                        TaskDescription = "测试计划",
                        TodoList = new List<TodoItem>
                        {
                            new TodoItem { Description = "第一步" },
                            new TodoItem { Description = "第二步" }
                        }
                    }
                },
                CancellationToken.None));

            var readyEvent = Assert.Single(events.Where(item => item.Type == AgentEventType.TodoBoardReady));
            Assert.Equal("T1", readyEvent.CurrentTodoId);
            Assert.Contains("\"id\":\"T1\"", readyEvent.BoardJson);
        }

        [Fact]
        public async Task RunAsync_AskModeHitsIterationBudget_EmitsMaxIterationsReachedAndDoesNotEmitTaskCompleted()
        {
            var llmClient = new LoopingToolLlmClient("probe_document", "{}");
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[]
                {
                    new FakeTool("probe_document", ToolPermission.ReadOnly, ToolCallResult.Ok("{\"ok\":true}"))
                });

            var events = await CollectAsync(orchestrator.RunAsync(
                "请持续读取文档并总结",
                new AgentRunOptions
                {
                    Mode = AgentMode.Ask,
                    EnableToolCalling = true,
                    MaxIterations = 1
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.MaxIterationsReached);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(1, llmClient.CallCount);
        }

        [Fact]
        public async Task RunAsync_PlanModeHitsIterationBudget_EmitsMaxIterationsReachedAndDoesNotEmitTaskCompleted()
        {
            var llmClient = new LoopingToolLlmClient("probe_document", "{}");
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[]
                {
                    new FakeTool("probe_document", ToolPermission.ReadOnly, ToolCallResult.Ok("{\"ok\":true}"))
                });

            var events = await CollectAsync(orchestrator.RunAsync(
                "请规划后再执行",
                new AgentRunOptions
                {
                    Mode = AgentMode.Plan,
                    EnableToolCalling = true,
                    MaxIterations = 1
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.MaxIterationsReached);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(1, llmClient.CallCount);
        }

        [Fact]
        public async Task RunAsync_AgentModeHitsIterationBudget_PausesTodoBoardAndUsesFixedBudget100()
        {
            var todoStore = new FakeTodoStore();
            var llmClient = new LoopingToolLlmClient("probe_document", "{}");
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[]
                {
                    new FakeTool("probe_document", ToolPermission.ReadOnly, ToolCallResult.Ok("{\"ok\":true}"))
                },
                todoStore: todoStore);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请长时间持续分析文档",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    MaxIterations = 200
                },
                CancellationToken.None));

            var maxIterationsEvent = Assert.Single(events.Where(item => item.Type == AgentEventType.MaxIterationsReached));
            Assert.Contains("100", maxIterationsEvent.Message);
            Assert.Contains(events, item => item.Type == AgentEventType.TodoBoardPaused);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(100, llmClient.CallCount);

            var board = todoStore.PeekBoard("doc1");
            Assert.NotNull(board);
            Assert.Equal(TodoBoardExecutionState.Paused, board.ExecutionState);
            Assert.Equal(TodoBoardRunOutcome.PausedByBudget, board.LastRunOutcome);
        }

        [Fact]
        public async Task RunAsync_RecoveryRequiredBoard_EmitsRecoveryEventBeforeTodoBoardReady()
        {
            var todoStore = new FakeTodoStore();
            await todoStore.SaveBoardAsync(
                new TodoBoard
                {
                    SchemaVersion = TodoBoard.CurrentSchemaVersion,
                    BoardId = "board-1",
                    DocumentPath = "doc1",
                    ExecutionState = TodoBoardExecutionState.RecoveryRequired,
                    LastRunOutcome = TodoBoardRunOutcome.Failed,
                    RecoveryReason = "上一次任务异常中断，请先选择恢复方式。",
                    LastErrorSummary = "模拟异常",
                    Items = new List<TodoBoardItem>
                    {
                        new TodoBoardItem { Id = "T1", Content = "第一步", Status = TodoItemStatus.InProgress, Order = 1 }
                    }
                },
                CancellationToken.None);

            var recoveryChannel = new FakeTodoRecoveryChannel(TodoBoardRecoveryDecision.RecoverExisting);
            var llmClient = new FakeLlmClient(new AgentMessage
            {
                Role = "assistant",
                Content = "已恢复并继续执行。"
            });
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                todoRecoveryChannel: recoveryChannel,
                todoStore: todoStore);

            var events = await CollectAsync(orchestrator.RunAsync(
                "继续执行上次任务",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true
                },
                CancellationToken.None));

            var recoveryIndex = events.FindIndex(item => item.Type == AgentEventType.TodoBoardRecoveryRequired);
            var readyIndex = events.FindIndex(item => item.Type == AgentEventType.TodoBoardReady);

            Assert.True(recoveryIndex >= 0);
            Assert.True(readyIndex > recoveryIndex);
            Assert.Equal(1, recoveryChannel.WaitCount);
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
        }

        [Fact]
        public async Task RunAsync_AgentModeCompleted_SucceedsAndDeletesTodoBoard()
        {
            var todoStore = new FakeTodoStore();
            var orchestrator = CreateOrchestrator(
                new FakeLlmClient(new AgentMessage
                {
                    Role = "assistant",
                    Content = "任务已执行完成。"
                }),
                CreateWritableHydrator(),
                todoStore: todoStore);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请执行当前任务",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.False(todoStore.Exists("doc1"));
        }

        [Fact]
        public async Task RunAsync_ModelThrowsAfterRunStarted_MarksTodoBoardAsRecoveryRequired()
        {
            var todoStore = new FakeTodoStore();
            var orchestrator = CreateOrchestrator(
                new ThrowingLlmClient(new InvalidOperationException("模拟 LLM 故障")),
                CreateWritableHydrator(),
                new ITool[]
                {
                    new FakeTool("probe_document", ToolPermission.ReadOnly, ToolCallResult.Ok("{\"ok\":true}"))
                },
                todoStore: todoStore);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await CollectAsync(orchestrator.RunAsync(
                    "继续执行任务",
                    new AgentRunOptions
                    {
                        Mode = AgentMode.Agent,
                        EnableToolCalling = true
                    },
                    CancellationToken.None));
            });

            var board = todoStore.PeekBoard("doc1");
            Assert.NotNull(board);
            Assert.Equal(TodoBoardExecutionState.RecoveryRequired, board.ExecutionState);
            Assert.Equal(TodoBoardRunOutcome.Failed, board.LastRunOutcome);
            Assert.Contains("模拟 LLM 故障", board.LastErrorSummary);
        }

        [Fact]
        public async Task RunAsync_AfterFiveEffectiveExecutionRounds_EmitsTodoReminderInjected()
        {
            var llmMessages = new List<AgentMessage>();
            for (var index = 0; index < 5; index++)
            {
                llmMessages.Add(CreateToolCallMessage(
                    CreateToolCall($"probe-{index + 1}", "probe_document", "{}")));
            }

            llmMessages.Add(new AgentMessage
            {
                Role = "assistant",
                Content = "任务已结束。"
            });

            var llmClient = new FakeLlmClient(llmMessages.ToArray());
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[]
                {
                    new FakeTool("probe_document", ToolPermission.ReadOnly, ToolCallResult.Ok("{\"ok\":true}"))
                });

            var events = await CollectAsync(orchestrator.RunAsync(
                "请持续分析文档",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    MaxIterations = 12
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.TodoReminderInjected);
            var lastRequestMessages = llmClient.RequestMessageSnapshots.LastOrDefault();
            Assert.NotNull(lastRequestMessages);
            Assert.NotEmpty(lastRequestMessages);
            Assert.Equal("system", lastRequestMessages[0].Role);
            Assert.DoesNotContain(
                lastRequestMessages.Skip(1),
                item => string.Equals(item.Role, "system", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                lastRequestMessages.Skip(1),
                item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)
                    && (item.Content ?? string.Empty).Contains("请持续维护 todo board"));
        }

        [Fact]
        public async Task RunAsync_TodoReadRound_DoesNotResetReminderCounter()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(CreateToolCall("probe-1", "probe_document", "{}")),
                CreateToolCallMessage(CreateToolCall("probe-2", "probe_document", "{}")),
                CreateToolCallMessage(CreateToolCall("probe-3", "probe_document", "{}")),
                CreateToolCallMessage(CreateToolCall("probe-4", "probe_document", "{}")),
                CreateToolCallMessage(CreateToolCall("todo-read-1", "todo_read", "{}")),
                CreateToolCallMessage(CreateToolCall("probe-5", "probe_document", "{}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "任务已结束。"
                });

            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[]
                {
                    new FakeTool("probe_document", ToolPermission.ReadOnly, ToolCallResult.Ok("{\"ok\":true}")),
                    new FakeTool("todo_read", ToolPermission.ReadOnly, ToolCallResult.Ok("{\"ok\":true}"))
                });

            var events = await CollectAsync(orchestrator.RunAsync(
                "继续执行并偶尔读取任务板",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    MaxIterations = 8
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.TodoReminderInjected);
        }

        [Fact]
        public async Task RunAsync_AfterVerifiedWriteWithoutTodoWriteNextRound_EmitsHighPriorityReminder()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的标题\"}]}")),
                CreateToolCallMessage(
                    CreateToolCall("probe-1", "probe_document", "{}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "任务已结束。"
                });
            var writeTool = new FakeTool(
                "patch_range",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 1 },
                    operationDescription: "已修改第 1 段。"));
            var verifyTool = new FakeTool(
                "verify_script",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":true,\"results\":[]}",
                    new[] { 1 },
                    operationDescription: "已完成改动验证。"));
            var probeTool = new FakeTool(
                "probe_document",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok("{\"ok\":true}"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, verifyTool, probeTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "先写入，再继续读取",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            var reminderEvent = Assert.Single(events.Where(item => item.Type == AgentEventType.TodoReminderInjected));
            Assert.Contains("上一轮已经发生文档写入", reminderEvent.Message);
            var lastRequestMessages = llmClient.RequestMessageSnapshots.LastOrDefault();
            Assert.NotNull(lastRequestMessages);
            Assert.Contains(
                lastRequestMessages,
                item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)
                    && (item.Content ?? string.Empty).Contains("上一轮已经发生文档写入"));
        }

        [Fact]
        public async Task RunAsync_ReminderIsInjectedAfterSkippedToolResults()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的标题\"}]}")),
                CreateToolCallMessage(
                    CreateToolCall("probe-1", "probe_document", "{}"),
                    CreateToolCall("write-2", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":2,\"text\":\"失败写入\"}]}"),
                    CreateToolCall("read-1", "read_section", "{\"heading\":\"第一章\"}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "任务已结束。"
                });
            var writeTool = new FakeTool(
                "patch_range",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 1 },
                    operationDescription: "已修改第 1 段。"),
                ToolCallResult.Error("patch_range", "写入失败"));
            var verifyTool = new FakeTool(
                "verify_script",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":true,\"results\":[]}",
                    new[] { 1 },
                    operationDescription: "已完成改动验证。"));
            var probeTool = new FakeTool(
                "probe_document",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok("{\"ok\":true}"));
            var readTool = new FakeTool(
                "read_section",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok("{\"ok\":true}"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, verifyTool, probeTool, readTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "写入后继续执行复杂流程",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.TodoReminderInjected);
            var lastRequestMessages = llmClient.RequestMessageSnapshots.LastOrDefault();
            Assert.NotNull(lastRequestMessages);
            var skippedIndex = lastRequestMessages.FindIndex(item =>
                string.Equals(item.Role, "tool", StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.ToolCallId, "read-1", StringComparison.OrdinalIgnoreCase)
                && (item.Content ?? string.Empty).Contains("[SKIPPED]"));
            var reminderIndex = lastRequestMessages.FindIndex(item =>
                string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)
                && (item.Content ?? string.Empty).Contains("上一轮已经发生文档写入"));

            Assert.True(skippedIndex >= 0);
            Assert.True(reminderIndex > skippedIndex);
        }

        [Fact]
        public async Task RunAsync_WriteToolRejectedByUser_EmitsSkippedAndDoesNotExecuteTool()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("tool-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":0,\"text\":\"新的内容\"}]}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成。"
                });
            var tool = new FakeTool("patch_range", ToolPermission.Write, ToolCallResult.Ok("{\"success\":true}"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { tool },
                new FakeConfirmationChannel(false, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请直接修改第一段",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ToolCallStarted && item.RequiresConfirmation);
            Assert.Contains(events, item => item.Type == AgentEventType.ToolCallSkipped);
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(0, tool.ExecutionCount);
            Assert.Null(undoScopeFactory.LastScope);
        }

        [Fact]
        public async Task RunAsync_WriteToolFailed_StopsSameRoundAndTransitionsToRepairState()
        {
            var llmClient = new FakeLlmClient(new AgentMessage
            {
                Role = "assistant",
                ToolCalls = new List<ToolCall>
                {
                    new ToolCall { Id = "1", Name = "patch_range", Input = "{\"operations\":[]}" },
                    new ToolCall { Id = "2", Name = "patch_range", Input = "{\"operations\":[]}" },
                    new ToolCall { Id = "3", Name = "patch_range", Input = "{\"operations\":[]}" }
                }
            });
            var tool = new FakeTool("patch_range", ToolPermission.Write, ToolCallResult.Error("patch_range", "failed"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { tool },
                new FakeConfirmationChannel(true, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "run multiple writes",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeRepairRequired);
            Assert.Contains(events, item => item.Type == AgentEventType.Error);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(1, tool.ExecutionCount);
            Assert.Equal(0, undoScopeFactory.LastScope.CommitCount);
            Assert.Equal(1, undoScopeFactory.LastScope.RollbackCount);
        }

        [Fact]
        public async Task RunAsync_WriteToolSucceeded_ImmediatelyVerifiesAndEmitsExecutedAndVerifiedEvents()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的标题\"}]}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成改写。"
                });
            var writeTool = new FakeTool(
                "patch_range",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 1 },
                    operationDescription: "已修改第 1 段。"));
            var verifyTool = new FakeTool(
                "verify_script",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":true,\"results\":[]}",
                    new[] { 1 },
                    operationDescription: "已完成改动验证。"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, verifyTool },
                new FakeConfirmationChannel(true, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请修改第二段",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            var changeExecuted = Assert.Single(events.FindAll(item => item.Type == AgentEventType.ChangeExecuted));
            var changeApplied = Assert.Single(events.FindAll(item => item.Type == AgentEventType.ChangeApplied));
            Assert.Equal("patch_range", changeExecuted.ToolName);
            Assert.Equal("已修改第 1 段。", changeExecuted.OperationDescription);
            Assert.Equal(new[] { 1 }, changeExecuted.AffectedParagraphs);
            Assert.Equal("patch_range", changeApplied.ToolName);
            Assert.Equal("已修改第 1 段。", changeApplied.OperationDescription);
            Assert.Equal(new[] { 1 }, changeApplied.AffectedParagraphs);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallStarted
                && item.ToolCallId == "write-1__auto_verify"
                && item.ToolName == "verify_script");
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallCompleted
                && item.ToolCallId == "write-1__auto_verify"
                && item.ToolSuccess);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeVerificationFailed);
            Assert.NotNull(writeTool.LastUndoScope);
            Assert.Equal(1, verifyTool.ExecutionCount);
            Assert.Equal(1, undoScopeFactory.LastScope.CommitCount);
            Assert.Equal(0, undoScopeFactory.LastScope.RollbackCount);
        }

        [Fact]
        public async Task RunAsync_WriteToolSucceededThenCalledNonVerifyTool_SkipsRemainingToolCallsInSameRound()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的标题\"}]}"),
                    CreateToolCall("read-1", "read_section", "{\"heading\":\"第一章\"}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成改写。"
                });
            var writeTool = new FakeTool(
                "patch_range",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 1 },
                    operationDescription: "已修改第 1 段。"));
            var verifyTool = new FakeTool(
                "verify_script",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":true,\"results\":[]}",
                    new[] { 1 },
                    operationDescription: "已完成改动验证。"));
            var readTool = new FakeTool(
                "read_section",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok("{\"heading\":\"第一章\"}"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, verifyTool, readTool },
                new FakeConfirmationChannel(true, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请修改第二段",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeExecuted);
            var changeApplied = Assert.Single(events.FindAll(item => item.Type == AgentEventType.ChangeApplied));
            Assert.Equal("patch_range", changeApplied.ToolName);
            Assert.Equal("已修改第 1 段。", changeApplied.OperationDescription);
            Assert.Equal(new[] { 1 }, changeApplied.AffectedParagraphs);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeVerificationFailed);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallStarted
                && item.ToolCallId == "write-1__auto_verify"
                && item.ToolName == "verify_script");
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallCompleted
                && item.ToolCallId == "write-1__auto_verify"
                && item.ToolSuccess);
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(1, verifyTool.ExecutionCount);
            Assert.Equal(0, readTool.ExecutionCount);
            Assert.Equal(1, undoScopeFactory.LastScope.CommitCount);
            Assert.Equal(0, undoScopeFactory.LastScope.RollbackCount);
        }

        [Fact]
        public async Task RunAsync_TodoWrite_DoesNotEnterDocumentWriteVerificationLifecycle()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("todo-1", "todo_write", "{\"action\":\"set_status\",\"id\":\"T1\",\"status\":\"completed\"}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "任务板已同步。"
                });
            var todoWriteTool = new FakeTool(
                "todo_write",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    metadata: new TodoToolMetadata
                    {
                        IsWriteOperation = true,
                        Operation = "set_status",
                        BoardJson = "{}",
                        CurrentTodoId = "T1",
                        CompletedSteps = 1,
                        TotalSteps = 1
                    },
                    operationDescription: "更新当前 Todo Board。"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { todoWriteTool },
                new FakeConfirmationChannel(false, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "请把当前步骤标记为已完成",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            var startedEvent = Assert.Single(events.Where(item => item.ToolCallId == "todo-1" && item.Type == AgentEventType.ToolCallStarted));
            Assert.False(startedEvent.RequiresConfirmation);
            Assert.Contains(events, item => item.Type == AgentEventType.ToolCallCompleted && item.ToolCallId == "todo-1" && item.ToolSuccess);
            Assert.Contains(events, item => item.Type == AgentEventType.TodoBoardUpdated);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeExecuted);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeVerificationFailed);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeRepairRequired);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.Error);
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(1, todoWriteTool.ExecutionCount);
            Assert.Null(todoWriteTool.LastUndoScope);
            Assert.Null(undoScopeFactory.LastScope);
        }

        [Fact]
        public async Task RunAsync_ModelExplicitlyCallsVerifyScript_DeniesInternalTool()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("verify-1", "verify_script", "{\"code\":\"return new { all_passed = true, results = new object[0] };\"}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已结束。"
                });
            var verifyTool = new FakeTool(
                "verify_script",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":true,\"results\":[]}",
                    new[] { 1 },
                    operationDescription: "已完成改动验证。"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { verifyTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "请验证标题是否正确",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallDenied
                && item.ToolName == "verify_script");
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(0, verifyTool.ExecutionCount);
        }

        [Fact]
        public async Task RunAsync_VerifyScriptDidNotPass_EmitsChangeVerificationFailed()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "patch_range", "{\"operations\":[{\"type\":\"replace_text\",\"paragraph_index\":1,\"text\":\"新的标题\"}]}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成改写。"
                });
            var writeTool = new FakeTool(
                "patch_range",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 1 },
                    operationDescription: "已修改第 1 段。"));
            var verifyTool = new FakeTool(
                "verify_script",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":false,\"results\":[{\"check_key\":\"title\",\"passed\":false}]}",
                    new[] { 1 },
                    operationDescription: "已完成改动验证。"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, verifyTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "请修改第二段",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeExecuted);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallStarted
                && item.ToolCallId == "write-1__auto_verify");
            var verificationFailed = Assert.Single(events.FindAll(item => item.Type == AgentEventType.ChangeVerificationFailed));
            Assert.Equal("patch_range", verificationFailed.ToolName);
            Assert.Equal("已修改第 1 段。", verificationFailed.OperationDescription);
            Assert.Equal(new[] { 1 }, verificationFailed.AffectedParagraphs);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.ChangeApplied);
            Assert.Contains(events, item => item.Type == AgentEventType.Error && item.Message.Contains("未完成修复"));
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
        }

        [Fact]
        public async Task RunAsync_WriteToolFailedThenRepairSucceeded_CompletesAfterVerification()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "execute_script", "{\"description\":\"调整标题\",\"write_code\":\"bad\",\"verify_code\":\"return new { all_passed = true, results = new object[0] };\"}")),
                CreateToolCallMessage(
                    CreateToolCall("write-2", "execute_script", "{\"description\":\"调整标题\",\"write_code\":\"fixed\",\"verify_code\":\"return new { all_passed = true, results = new object[0] };\"}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已完成修复。"
                });
            var writeTool = new FakeTool(
                "execute_script",
                ToolPermission.Write,
                ToolCallResult.Error("execute_script", "脚本运行失败。"),
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 0 },
                    operationDescription: "已调整标题。"));
            var verifyTool = new FakeTool(
                "verify_script",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok(
                    "{\"all_passed\":true,\"results\":[]}",
                    new[] { 0 },
                    operationDescription: "已完成改动验证。"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, verifyTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "请修复标题格式",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeRepairRequired);
            Assert.Contains(events, item => item.Type == AgentEventType.ChangeExecuted);
            Assert.Contains(events, item => item.Type == AgentEventType.ChangeApplied);
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(2, writeTool.ExecutionCount);
            Assert.Equal(1, verifyTool.ExecutionCount);
        }

        [Fact]
        public async Task RunAsync_WriteToolFailedAndModelStopped_FailsTaskWithoutTaskCompleted()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "execute_script", "{\"description\":\"调整标题\",\"write_code\":\"bad\",\"verify_code\":\"return new { all_passed = true, results = new object[0] };\"}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "无法继续。"
                });
            var writeTool = new FakeTool(
                "execute_script",
                ToolPermission.Write,
                ToolCallResult.Error("execute_script", "脚本运行失败。"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "请调整标题格式",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item =>
                item.Type == AgentEventType.ChangeRepairRequired
                && item.ToolName == "execute_script");
            Assert.Contains(events, item => item.Type == AgentEventType.Error && item.Message.Contains("未完成修复"));
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
        }

        [Fact]
        public async Task RunAsync_ExecuteScriptMissingVerifyCode_IsTreatedAsWriteFailure()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "execute_script", "{\"description\":\"fix title\",\"write_code\":\"ok\",\"affected_paragraphs\":[0]}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "done"
                });
            var writeTool = new FakeTool(
                "execute_script",
                ToolPermission.Write,
                ToolCallResult.Ok(
                    "{\"success\":true}",
                    new[] { 0 },
                    operationDescription: "updated title"));
            var undoScopeFactory = new FakeUndoScopeFactory();
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool },
                new FakeConfirmationChannel(true, true),
                undoScopeFactory);

            var events = await CollectAsync(orchestrator.RunAsync(
                "fix title format",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeExecuted);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ChangeVerificationFailed
                && item.Message.Contains("verify_code"));
            Assert.Contains(events, item => item.Type == AgentEventType.Error);
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(0, undoScopeFactory.LastScope.CommitCount);
            Assert.Equal(1, undoScopeFactory.LastScope.RollbackCount);
        }

        [Fact]
        public async Task RunAsync_PendingWriteRepair_DeniesNonProbeReadToolUntilWriteToolRepairs()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "execute_script", "{\"description\":\"fix title\",\"write_code\":\"bad\",\"verify_code\":\"return new { all_passed = true, results = new object[0] };\"}")),
                CreateToolCallMessage(
                    CreateToolCall("read-1", "read_section", "{\"heading\":\"Section 1\"}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "cannot continue"
                });
            var writeTool = new FakeTool(
                "execute_script",
                ToolPermission.Write,
                ToolCallResult.Error("execute_script", "script failed"));
            var readTool = new FakeTool(
                "read_section",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok("{\"heading\":\"Section 1\"}"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, readTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "repair current step",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeRepairRequired);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallDenied
                && item.ToolName == "read_section");
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(1, writeTool.ExecutionCount);
            Assert.Equal(0, readTool.ExecutionCount);
        }

        [Fact]
        public async Task RunAsync_PendingWriteRepair_AllowsReadScriptProbeBeforeNextRepairTurn()
        {
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(
                    CreateToolCall("write-1", "execute_script", "{\"description\":\"fix title\",\"write_code\":\"bad\",\"verify_code\":\"return new { all_passed = true, results = new object[0] };\"}")),
                CreateToolCallMessage(
                    CreateToolCall("probe-1", "read_script", "{\"description\":\"probe failed paragraphs\",\"code\":\"return new { paragraph_count = 3 };\"}")),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "cannot continue"
                });
            var writeTool = new FakeTool(
                "execute_script",
                ToolPermission.Write,
                ToolCallResult.Error("execute_script", "script failed"));
            var probeTool = new FakeTool(
                "read_script",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok("{\"output\":\"{\\\"paragraph_count\\\":3}\",\"log_output\":\"\",\"return_value_type\":\"<>f__AnonymousType0\"}"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { writeTool, probeTool },
                new FakeConfirmationChannel(true, true));

            var events = await CollectAsync(orchestrator.RunAsync(
                "repair current step",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true,
                    RequireConfirmationForScripts = true
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ChangeRepairRequired);
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallStarted
                && item.ToolName == "read_script");
            Assert.Contains(events, item =>
                item.Type == AgentEventType.ToolCallCompleted
                && item.ToolName == "read_script"
                && item.ToolSuccess);
            Assert.DoesNotContain(events, item =>
                item.Type == AgentEventType.ToolCallDenied
                && item.ToolName == "read_script");
            Assert.DoesNotContain(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Equal(1, writeTool.ExecutionCount);
            Assert.Equal(1, probeTool.ExecutionCount);
        }

        [Fact]
        public async Task RunAsync_MoreThanTwentyToolCalls_TruncatesAtTwenty()
        {
            var toolCalls = Enumerable.Range(1, 21)
                .Select(index => CreateToolCall(index.ToString(), "read_section", "{\"heading\":\"第一章\"}"))
                .ToArray();
            var llmClient = new FakeLlmClient(
                CreateToolCallMessage(toolCalls),
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "已读取。"
                });
            var readTool = new FakeTool(
                "read_section",
                ToolPermission.ReadOnly,
                ToolCallResult.Ok("{\"heading\":\"第一章\"}"));
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                new ITool[] { readTool });

            var events = await CollectAsync(orchestrator.RunAsync(
                "请大量读取章节",
                new AgentRunOptions
                {
                    Mode = AgentMode.Agent,
                    EnableToolCalling = true
                },
                CancellationToken.None));

            Assert.Equal(20, readTool.ExecutionCount);
            Assert.Equal(20, events.Count(item => item.Type == AgentEventType.ToolCallStarted));
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
        }

        [Fact]
        public async Task RunAsync_ContextExceeded_CompressesHistoryAndContinues()
        {
            var llmClient = new FakeLlmClient(new AgentMessage
            {
                Role = "assistant",
                Content = "压缩后继续完成。"
            });
            var conversationStore = new FakeConversationStore(new[]
            {
                CreateHistoryMessage("user", "历史用户消息 1"),
                CreateHistoryMessage("assistant", "历史助手消息 1"),
                CreateHistoryMessage("user", "历史用户消息 2"),
                CreateHistoryMessage("assistant", "历史助手消息 2"),
                CreateHistoryMessage("user", "历史用户消息 3"),
                CreateHistoryMessage("assistant", "历史助手消息 3"),
                CreateHistoryMessage("user", "历史用户消息 4"),
                CreateHistoryMessage("assistant", "历史助手消息 4")
            });
            var orchestrator = CreateOrchestrator(
                llmClient,
                CreateWritableHydrator(),
                conversationStore: conversationStore);

            var events = await CollectAsync(orchestrator.RunAsync(
                "继续处理当前任务",
                new AgentRunOptions
                {
                    Mode = AgentMode.Ask,
                    EnableToolCalling = false,
                    CompactionThreshold = 9000
                },
                CancellationToken.None));

            Assert.Contains(events, item => item.Type == AgentEventType.ContextCompacted);
            Assert.Contains(events, item => item.Type == AgentEventType.TaskCompleted);
            Assert.Contains(conversationStore.LastEstimatedMessages, item => item.IsCompressedSummary);
        }

        private static AgentOrchestrator CreateOrchestrator(
            ILlmClient llmClient,
            IContextHydrator contextHydrator,
            IEnumerable<ITool> tools = null,
            IConfirmationChannel confirmationChannel = null,
            IUndoScopeFactory undoScopeFactory = null,
            IConversationStore conversationStore = null,
            IQuestionChannel questionChannel = null,
            ITodoRecoveryChannel todoRecoveryChannel = null,
            ITodoStore todoStore = null)
        {
            var registry = new ToolRegistry();
            var effectiveTodoStore = todoStore ?? new FakeTodoStore();
            var todoManager = new TodoManager(effectiveTodoStore);
            if (tools != null)
            {
                foreach (var tool in tools)
                {
                    if (tool != null)
                    {
                        registry.Register(tool);
                    }
                }
            }

            return new AgentOrchestrator(
                llmClient,
                contextHydrator,
                conversationStore ?? new InMemoryConversationStore(),
                new SystemPromptBuilder(string.Empty),
                registry,
                new PermissionGuard(registry),
                confirmationChannel ?? new FakeConfirmationChannel(true, false),
                undoScopeFactory ?? new FakeUndoScopeFactory(),
                new ConversationCompressor(),
                questionChannel,
                todoRecoveryChannel ?? new FakeTodoRecoveryChannel(TodoBoardRecoveryDecision.RecoverExisting),
                todoManager,
                new TodoReminderService());
        }

        private static FakeContextHydrator CreateWritableHydrator()
        {
            return new FakeContextHydrator(new DocumentContext
            {
                DocumentPath = "doc1",
                DocumentStatus = new DocumentStatus
                {
                    IsWritable = true
                }
            });
        }

        private static AgentMessage CreateToolCallMessage(params ToolCall[] toolCalls)
        {
            return new AgentMessage
            {
                Role = "assistant",
                ToolCalls = toolCalls == null
                    ? new List<ToolCall>()
                    : new List<ToolCall>(toolCalls)
            };
        }

        private static ToolCall CreateToolCall(string id, string toolName, string input)
        {
            return new ToolCall
            {
                Id = id,
                Name = toolName,
                Input = input
            };
        }

        private static AgentMessage CreateHistoryMessage(string role, string content)
        {
            return new AgentMessage
            {
                Role = role,
                Content = content
            };
        }

        private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> stream)
        {
            var results = new List<AgentEvent>();
            await foreach (var item in stream)
            {
                results.Add(item);
            }

            return results;
        }

        private sealed class FakeTodoStore : ITodoStore
        {
            private readonly Dictionary<string, TodoBoard> _boards =
                new Dictionary<string, TodoBoard>(System.StringComparer.OrdinalIgnoreCase);

            public Task<TodoBoard> GetBoardAsync(string documentPath, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _boards.TryGetValue(documentPath ?? "__active_document__", out var board);
                return Task.FromResult(board);
            }

            public Task SaveBoardAsync(TodoBoard board, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _boards[board.DocumentPath ?? "__active_document__"] = board;
                return Task.CompletedTask;
            }

            public Task DeleteBoardAsync(string documentPath, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _boards.Remove(documentPath ?? "__active_document__");
                return Task.CompletedTask;
            }

            public Task<bool> ExistsAsync(string documentPath, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_boards.ContainsKey(documentPath ?? "__active_document__"));
            }

            public TodoBoard PeekBoard(string documentPath)
            {
                _boards.TryGetValue(documentPath ?? "__active_document__", out var board);
                return board;
            }

            public bool Exists(string documentPath)
            {
                return _boards.ContainsKey(documentPath ?? "__active_document__");
            }
        }

        private sealed class FakeTodoRecoveryChannel : ITodoRecoveryChannel
        {
            private readonly TodoBoardRecoveryDecision _decision;

            public FakeTodoRecoveryChannel(TodoBoardRecoveryDecision decision, bool isAvailable = true)
            {
                _decision = decision;
                IsAvailable = isAvailable;
            }

            public bool IsAvailable { get; }

            public string LastRecoveryRequestId { get; private set; }

            public int WaitCount { get; private set; }

            public Task<TodoBoardRecoveryDecision> WaitForDecisionAsync(string recoveryRequestId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastRecoveryRequestId = recoveryRequestId;
                WaitCount++;
                return Task.FromResult(_decision);
            }
        }

        private sealed class FakeLlmClient : ILlmClient
        {
            private readonly Queue<AgentMessage> _responses = new Queue<AgentMessage>();
            public List<List<AgentMessage>> RequestMessageSnapshots { get; } = new List<List<AgentMessage>>();
            public int CallCount { get; private set; }

            public FakeLlmClient(params AgentMessage[] responses)
            {
                foreach (var response in responses)
                {
                    _responses.Enqueue(response);
                }
            }

            public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                _ = messages;
                _ = model;
                cancellationToken.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                yield break;
            }

            public Task<AgentMessage> ChatCompletionWithToolsAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                IReadOnlyList<ToolDefinition> tools,
                System.Action<string> onStreamChunk,
                CancellationToken cancellationToken)
            {
                RequestMessageSnapshots.Add(messages == null
                    ? new List<AgentMessage>()
                    : new List<AgentMessage>(messages.Select(CloneMessage)));
                CallCount++;
                _ = model;
                _ = tools;
                cancellationToken.ThrowIfCancellationRequested();

                var response = _responses.Count == 0
                    ? new AgentMessage
                    {
                        Role = "assistant",
                        Content = "done"
                    }
                    : _responses.Dequeue();
                if (!string.IsNullOrWhiteSpace(response.Content))
                {
                    onStreamChunk?.Invoke(response.Content);
                }

                return Task.FromResult(response);
            }

            private static AgentMessage CloneMessage(AgentMessage message)
            {
                return new AgentMessage
                {
                    Role = message == null ? string.Empty : message.Role,
                    Content = message == null ? string.Empty : message.Content,
                    ReasoningContent = message == null ? string.Empty : message.ReasoningContent,
                    ToolCallId = message == null ? string.Empty : message.ToolCallId,
                    Name = message == null ? string.Empty : message.Name,
                    IsCompressedSummary = message != null && message.IsCompressedSummary,
                    ToolName = message == null ? string.Empty : message.ToolName,
                    RawToolInput = message == null ? string.Empty : message.RawToolInput,
                    ToolSuccess = message != null && message.ToolSuccess,
                    ToolCalls = message == null || message.ToolCalls == null
                        ? new List<ToolCall>()
                        : new List<ToolCall>(message.ToolCalls)
                };
            }
        }

        private sealed class LoopingToolLlmClient : ILlmClient
        {
            private readonly string _toolName;
            private readonly string _toolInput;

            public LoopingToolLlmClient(string toolName, string toolInput)
            {
                _toolName = toolName;
                _toolInput = toolInput;
            }

            public int CallCount { get; private set; }

            public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                _ = messages;
                _ = model;
                cancellationToken.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                yield break;
            }

            public Task<AgentMessage> ChatCompletionWithToolsAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                IReadOnlyList<ToolDefinition> tools,
                System.Action<string> onStreamChunk,
                CancellationToken cancellationToken)
            {
                _ = messages;
                _ = model;
                _ = tools;
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;

                return Task.FromResult(CreateToolCallMessage(
                    CreateToolCall($"loop-{CallCount}", _toolName, _toolInput)));
            }
        }

        private sealed class FakeContextHydrator : IContextHydrator
        {
            private readonly DocumentContext _context;

            public FakeContextHydrator(DocumentContext context)
            {
                _context = context;
            }

            public Task<DocumentContext> HydrateAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_context);
            }
        }

        private sealed class ThrowingLlmClient : ILlmClient
        {
            private readonly Exception _exception;

            public ThrowingLlmClient(Exception exception)
            {
                _exception = exception;
            }

            public async IAsyncEnumerable<string> ChatCompletionStreamAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                _ = messages;
                _ = model;
                cancellationToken.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                throw _exception;
#pragma warning disable CS0162
                yield break;
#pragma warning restore CS0162
            }

            public Task<AgentMessage> ChatCompletionWithToolsAsync(
                IReadOnlyList<AgentMessage> messages,
                string model,
                IReadOnlyList<ToolDefinition> tools,
                System.Action<string> onStreamChunk,
                CancellationToken cancellationToken)
            {
                _ = messages;
                _ = model;
                _ = tools;
                _ = onStreamChunk;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromException<AgentMessage>(_exception);
            }
        }

        private sealed class FakeTool : ITool
        {
            private readonly JsonElement _schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
            private readonly Queue<ToolCallResult> _results = new Queue<ToolCallResult>();
            private ToolCallResult _lastResult;

            public FakeTool(string name, ToolPermission permission, params ToolCallResult[] results)
            {
                Name = name;
                RequiredPermission = permission;
                if (results != null)
                {
                    foreach (var result in results)
                    {
                        _results.Enqueue(result);
                        _lastResult = result;
                    }
                }
            }

            public string Name { get; }

            public string Description => Name;

            public ToolPermission RequiredPermission { get; }

            public bool IsVisibleToModel { get; set; } = true;

            public JsonElement InputSchema => _schema;

            public int ExecutionCount { get; private set; }

            public IUndoScope LastUndoScope { get; private set; }

            public Task<ToolCallResult> ExecuteAsync(JsonElement input, IUndoScope undoScope, CancellationToken cancellationToken)
            {
                _ = input;
                cancellationToken.ThrowIfCancellationRequested();
                ExecutionCount++;
                LastUndoScope = undoScope;
                if (_results.Count > 0)
                {
                    _lastResult = _results.Dequeue();
                }

                return Task.FromResult(_lastResult ?? ToolCallResult.Ok("{}"));
            }
        }

        private sealed class FakeConfirmationChannel : IConfirmationChannel
        {
            private readonly bool _confirmationResult;

            public FakeConfirmationChannel(bool confirmationResult, bool isAvailable)
            {
                _confirmationResult = confirmationResult;
                IsAvailable = isAvailable;
            }

            public bool IsAvailable { get; }

            public Task<bool> WaitForConfirmationAsync(string toolCallId, CancellationToken cancellationToken)
            {
                _ = toolCallId;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(_confirmationResult);
            }
        }

        private sealed class FakeQuestionChannel : IQuestionChannel
        {
            private readonly string _answer;

            public FakeQuestionChannel(string answer, bool isAvailable = true)
            {
                _answer = answer;
                IsAvailable = isAvailable;
            }

            public bool IsAvailable { get; }

            public string LastQuestionId { get; private set; }

            public Task<string> WaitForAnswerAsync(string questionId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastQuestionId = questionId;
                return Task.FromResult(_answer);
            }
        }

        private sealed class FakeUndoScopeFactory : IUndoScopeFactory
        {
            public FakeUndoScope LastScope { get; private set; }

            public Task<IUndoScope> BeginTaskUndoAsync(string operationName, CancellationToken cancellationToken)
            {
                _ = operationName;
                cancellationToken.ThrowIfCancellationRequested();
                LastScope = new FakeUndoScope();
                return Task.FromResult<IUndoScope>(LastScope);
            }
        }

        private sealed class FakeUndoScope : IUndoScope
        {
            public int CommitCount { get; private set; }

            public int RollbackCount { get; private set; }

            public void Commit()
            {
                CommitCount++;
            }

            public void Rollback()
            {
                RollbackCount++;
            }

            public void Dispose()
            {
            }
        }

        private sealed class FakeConversationStore : IConversationStore
        {
            private readonly List<AgentMessage> _history;

            public FakeConversationStore(IEnumerable<AgentMessage> history)
            {
                _history = history == null
                    ? new List<AgentMessage>()
                    : new List<AgentMessage>(history);
                LastEstimatedMessages = new List<AgentMessage>();
            }

            public List<AgentMessage> LastEstimatedMessages { get; private set; }

            public Task AppendUserMessageAsync(string documentPath, AgentMessage message, CancellationToken cancellationToken)
            {
                _ = documentPath;
                cancellationToken.ThrowIfCancellationRequested();
                _history.Add(CloneMessage(message));
                return Task.CompletedTask;
            }

            public Task AppendAssistantMessageAsync(string documentPath, AgentMessage message, CancellationToken cancellationToken)
            {
                _ = documentPath;
                cancellationToken.ThrowIfCancellationRequested();
                _history.Add(CloneMessage(message));
                return Task.CompletedTask;
            }

            public Task AppendToolResultAsync(
                string documentPath,
                string toolCallId,
                string toolName,
                string rawInput,
                ToolCallResult result,
                CancellationToken cancellationToken)
            {
                _ = documentPath;
                _ = rawInput;
                cancellationToken.ThrowIfCancellationRequested();
                _history.Add(new AgentMessage
                {
                    Role = "tool",
                    ToolCallId = toolCallId ?? string.Empty,
                    ToolName = toolName ?? string.Empty,
                    Content = result == null ? string.Empty : result.Output,
                    ToolSuccess = result != null && result.Success
                });
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<AgentMessage>> GetHistoryAsync(string documentPath, CancellationToken cancellationToken)
            {
                _ = documentPath;
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult((IReadOnlyList<AgentMessage>)_history.ConvertAll(CloneMessage));
            }

            public int EstimateTokenCount(IReadOnlyCollection<AgentMessage> messages)
            {
                LastEstimatedMessages = messages == null
                    ? new List<AgentMessage>()
                    : new List<AgentMessage>(messages.Select(CloneMessage));
                return (messages == null ? 0 : messages.Count) * 1000;
            }

            private static AgentMessage CloneMessage(AgentMessage message)
            {
                return new AgentMessage
                {
                    Role = message == null ? string.Empty : message.Role,
                    Content = message == null ? string.Empty : message.Content,
                    ReasoningContent = message == null ? string.Empty : message.ReasoningContent,
                    ToolCallId = message == null ? string.Empty : message.ToolCallId,
                    Name = message == null ? string.Empty : message.Name,
                    IsCompressedSummary = message != null && message.IsCompressedSummary,
                    ToolName = message == null ? string.Empty : message.ToolName,
                    RawToolInput = message == null ? string.Empty : message.RawToolInput,
                    ToolSuccess = message != null && message.ToolSuccess,
                    ToolCalls = message == null || message.ToolCalls == null
                        ? new List<ToolCall>()
                        : new List<ToolCall>(message.ToolCalls)
                };
            }
        }
    }
}
