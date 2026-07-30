using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using SmartWord.Core.Models;
using SmartWord.Infrastructure.LlmClients;
using Xunit;

namespace SmartWord.Application.Tests.Infrastructure
{
    public class OpenAiCompatibleClientMessagePayloadTests
    {
        [Fact]
        public void BuildMessagesPayload_ToolMessage_DoesNotSerializeNameField()
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage
                {
                    Role = "tool",
                    ToolCallId = "call_123",
                    Name = "probe_document",
                    Content = "{\"ok\":true}"
                }
            };

            var payload = InvokeBuildMessagesPayload(messages);
            var toolMessage = Assert.IsType<JObject>(payload[0]);

            Assert.Equal("tool", toolMessage["role"]?.Value<string>());
            Assert.Equal("call_123", toolMessage["tool_call_id"]?.Value<string>());
            Assert.Null(toolMessage["name"]);
        }

        [Fact]
        public void BuildMessagesPayload_AssistantToolCall_PreservesReasoningContent()
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage
                {
                    Role = "assistant",
                    Content = "\n\n",
                    ReasoningContent = "先确认文档名，再回答。",
                    ToolCalls = new List<ToolCall>
                    {
                        new ToolCall
                        {
                            Id = "call_456",
                            Name = "probe_document",
                            Input = "{\"include_stats\":true}"
                        }
                    }
                }
            };

            var payload = InvokeBuildMessagesPayload(messages);
            var assistantMessage = Assert.IsType<JObject>(payload[0]);

            Assert.Equal("assistant", assistantMessage["role"]?.Value<string>());
            Assert.Equal("\n\n", assistantMessage["content"]?.Value<string>());
            Assert.Equal("先确认文档名，再回答。", assistantMessage["reasoning_content"]?.Value<string>());
            Assert.Equal("function", assistantMessage["tool_calls"]?[0]?["type"]?.Value<string>());
            Assert.Equal("probe_document", assistantMessage["tool_calls"]?[0]?["function"]?["name"]?.Value<string>());
        }

        [Fact]
        public void BuildMessagesPayload_InvalidToolMessage_IsSkipped()
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage
                {
                    Role = "user",
                    Content = "文件名是什么"
                },
                new AgentMessage
                {
                    Role = "tool",
                    Content = "{\"ok\":true}"
                }
            };

            var payload = InvokeBuildMessagesPayload(messages);

            Assert.Single(payload);
            Assert.Equal("user", payload[0]?["role"]?.Value<string>());
        }

        [Fact]
        public void BuildMessagesPayload_MultipleSystemMessages_AreMergedIntoLeadingSystemMessage()
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage
                {
                    Role = "system",
                    Content = "基础系统提示"
                },
                new AgentMessage
                {
                    Role = "user",
                    Content = "继续执行"
                },
                new AgentMessage
                {
                    Role = "system",
                    Content = "运行时提醒"
                }
            };

            var payload = InvokeBuildMessagesPayload(messages);

            Assert.Equal(2, payload.Count);
            Assert.Equal("system", payload[0]?["role"]?.Value<string>());
            Assert.Contains("基础系统提示", payload[0]?["content"]?.Value<string>());
            Assert.Contains("运行时提醒", payload[0]?["content"]?.Value<string>());
            Assert.Equal("user", payload[1]?["role"]?.Value<string>());
        }

        [Fact]
        public void BuildRequestJson_MissingUserMessage_ThrowsBeforeSendingProviderRequest()
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "assistant", Content = "准备执行。" },
                new AgentMessage
                {
                    Role = "tool",
                    ToolCallId = "call_1",
                    Content = "{\"ok\":true}"
                }
            };

            var exception = AssertBuildRequestJsonInvalid(messages);

            Assert.Contains("真实 role=user", exception.Message);
        }

        [Fact]
        public void BuildRequestJson_OnlyInternalObservationUser_ThrowsBeforeSendingProviderRequest()
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage
                {
                    Role = "user",
                    Content = "[SmartWord 自动验证结果] 当前写步骤已验证通过。",
                    IsInternalObservation = true,
                    InternalObservationKind = "auto_verify_result"
                }
            };

            var exception = AssertBuildRequestJsonInvalid(messages);

            Assert.Contains("真实 role=user", exception.Message);
        }

        [Fact]
        public void BuildRequestJson_RealUserWithInternalObservation_SerializesRequest()
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "继续执行正文格式修改" },
                new AgentMessage
                {
                    Role = "user",
                    Content = "[SmartWord 自动验证结果] 当前写步骤已验证通过。",
                    IsInternalObservation = true,
                    InternalObservationKind = "auto_verify_result"
                }
            };

            var requestJson = InvokeBuildRequestJson(messages);

            Assert.Contains("继续执行正文格式修改", requestJson);
            Assert.Contains("SmartWord 自动验证结果", requestJson);
        }

        [Fact]
        public void BuildRequestJson_OrphanToolMessage_ThrowsBeforeSendingProviderRequest()
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "继续执行" },
                new AgentMessage
                {
                    Role = "tool",
                    ToolCallId = "call_1",
                    Content = "{\"ok\":true}"
                }
            };

            var exception = AssertBuildRequestJsonInvalid(messages);

            Assert.Contains("孤立的 tool 消息", exception.Message);
        }

        [Fact]
        public void BuildRequestJson_CompleteToolCallPair_SerializesRequest()
        {
            var messages = new List<AgentMessage>
            {
                new AgentMessage { Role = "system", Content = "system" },
                new AgentMessage { Role = "user", Content = "继续执行" },
                new AgentMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ToolCalls = new List<ToolCall>
                    {
                        new ToolCall
                        {
                            Id = "call_1",
                            Name = "probe_document",
                            Input = "{}"
                        }
                    }
                },
                new AgentMessage
                {
                    Role = "tool",
                    ToolCallId = "call_1",
                    Content = "{\"ok\":true}"
                }
            };

            var requestJson = InvokeBuildRequestJson(messages);

            Assert.Contains("\"role\":\"user\"", requestJson);
            Assert.Contains("\"tool_call_id\":\"call_1\"", requestJson);
        }

        [Fact]
        public void BuildRequestJson_StrictToolChoice_SerializesRequired()
        {
            var requestJson = InvokeBuildRequestJson(
                new[] { new AgentMessage { Role = "user", Content = "总结这篇文档" } },
                new[] { new ToolDefinition { Name = "probe_document" } },
                true);

            var payload = JObject.Parse(requestJson);

            Assert.Equal("required", payload["tool_choice"]?.Value<string>());
        }

        [Fact]
        public void BuildRequestJson_DefaultToolChoice_SerializesAuto()
        {
            var requestJson = InvokeBuildRequestJson(
                new[] { new AgentMessage { Role = "user", Content = "总结这篇文档" } },
                new[] { new ToolDefinition { Name = "probe_document" } },
                false);

            var payload = JObject.Parse(requestJson);

            Assert.Equal("auto", payload["tool_choice"]?.Value<string>());
        }

        [Fact]
        public void SummarizeBody_LongBody_IsTruncated()
        {
            var method = typeof(OpenAiCompatibleClient).GetMethod(
                "SummarizeBody",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var body = new string('a', 400);
            var summary = Assert.IsType<string>(method.Invoke(null, new object[] { body }));

            Assert.Contains("len=400", summary);
            Assert.EndsWith("...", summary);
            Assert.DoesNotContain(body, summary);
        }

        private static JArray InvokeBuildMessagesPayload(IReadOnlyList<AgentMessage> messages)
        {
            var method = typeof(OpenAiCompatibleClient).GetMethod(
                "BuildMessagesPayload",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var capability = new ModelCapability
            {
                Model = "deepseek-ai/DeepSeek-V3.2",
                SupportsToolCalling = true,
                RequiresReasoningContentReplay = true
            };

            var result = method.Invoke(null, new object[] { messages, capability });
            return Assert.IsType<JArray>(result);
        }

        private static string InvokeBuildRequestJson(
            IReadOnlyList<AgentMessage> messages,
            IReadOnlyList<ToolDefinition> tools = null,
            bool requireToolCall = false)
        {
            var method = typeof(OpenAiCompatibleClient).GetMethod(
                "BuildRequestJson",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);

            var capability = new ModelCapability
            {
                Model = "Qwen/Qwen3.5-397B-A17B",
                SupportsToolCalling = true
            };

            var result = method.Invoke(
                null,
                new object[]
                {
                    "Qwen/Qwen3.5-397B-A17B",
                    messages,
                    tools,
                    capability,
                    requireToolCall
                });
            return Assert.IsType<string>(result);
        }

        private static InvalidOperationException AssertBuildRequestJsonInvalid(IReadOnlyList<AgentMessage> messages)
        {
            var exception = Assert.Throws<TargetInvocationException>(() => InvokeBuildRequestJson(messages));
            return Assert.IsType<InvalidOperationException>(exception.InnerException);
        }
    }
}
