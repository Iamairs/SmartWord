using System.Linq;
using System.Text.Json;
using SmartWord.Application.Tools;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using Xunit;

namespace SmartWord.Application.Tests.Tools
{
    public class ToolRegistryTests
    {
        [Fact]
        public void GetToolDefinitions_AskModeOnlyReturnsReadOnlyTools()
        {
            var registry = new ToolRegistry();
            registry.Register(new FakeTool("probe_document", ToolPermission.ReadOnly));
            registry.Register(new FakeTool("read_section", ToolPermission.ReadOnly));
            registry.Register(new FakeTool("patch_range", ToolPermission.Write));

            var definitions = registry.GetToolDefinitions(AgentMode.Ask);

            Assert.Equal(2, definitions.Count);
            Assert.DoesNotContain(definitions, item => item.Name == "patch_range");
        }

        [Fact]
        public void GetToolDefinitions_AgentModeReturnsAllTools()
        {
            var registry = new ToolRegistry();
            registry.Register(new FakeTool("probe_document", ToolPermission.ReadOnly));
            registry.Register(new FakeTool("patch_range", ToolPermission.Write));

            var definitions = registry.GetToolDefinitions(AgentMode.Agent);

            Assert.Equal(2, definitions.Count);
            Assert.Contains(definitions, item => item.Name == "patch_range");
        }

        [Fact]
        public void GetToolDefinitions_AgentModeHidesInternalTools()
        {
            var registry = new ToolRegistry();
            registry.Register(new FakeTool("read_script", ToolPermission.ReadOnly));
            registry.Register(new FakeTool("verify_script", ToolPermission.ReadOnly, isVisibleToModel: false));

            var definitions = registry.GetToolDefinitions(AgentMode.Agent);

            Assert.Single(definitions);
            Assert.DoesNotContain(definitions, item => item.Name == "verify_script");
            Assert.Contains(definitions, item => item.Name == "read_script");
        }

        [Fact]
        public void GetTool_RegisteredName_ReturnsTool()
        {
            var registry = new ToolRegistry();
            registry.Register(new FakeTool("grep_document", ToolPermission.ReadOnly));

            var tool = registry.GetTool("grep_document");

            Assert.NotNull(tool);
            Assert.Equal("grep_document", tool.Name);
        }

        [Fact]
        public void Register_DuplicateName_UsesLatestInstance()
        {
            var registry = new ToolRegistry();
            registry.Register(new FakeTool("probe_document", ToolPermission.ReadOnly, "old"));
            registry.Register(new FakeTool("probe_document", ToolPermission.ReadOnly, "new"));

            var tool = registry.GetTool("probe_document");

            Assert.NotNull(tool);
            Assert.Equal("new", tool.Description);
        }

        private sealed class FakeTool : ITool
        {
            private readonly JsonElement _schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

            public FakeTool(string name, ToolPermission permission, string description = null, bool isVisibleToModel = true)
            {
                Name = name;
                RequiredPermission = permission;
                Description = description ?? name;
                IsVisibleToModel = isVisibleToModel;
            }

            public string Name { get; }

            public string Description { get; }

            public ToolPermission RequiredPermission { get; }

            public bool IsVisibleToModel { get; }

            public JsonElement InputSchema => _schema;

            public System.Threading.Tasks.Task<ToolCallResult> ExecuteAsync(
                JsonElement input,
                IUndoScope undoScope,
                System.Threading.CancellationToken cancellationToken)
            {
                return System.Threading.Tasks.Task.FromResult(ToolCallResult.Ok("{}"));
            }
        }
    }
}
