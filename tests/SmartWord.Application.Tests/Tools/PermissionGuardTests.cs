using System.Text.Json;
using SmartWord.Application.Tools;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using Xunit;

namespace SmartWord.Application.Tests.Tools
{
    public class PermissionGuardTests
    {
        [Fact]
        public void IsAllowed_AskModeReadOnlyTool_ReturnsTrue()
        {
            var registry = new ToolRegistry();
            registry.Register(new FakeTool("probe_document", ToolPermission.ReadOnly));
            var guard = new PermissionGuard(registry);

            var allowed = guard.IsAllowed("probe_document", AgentMode.Ask);

            Assert.True(allowed);
        }

        [Fact]
        public void IsAllowed_AskModeWriteTool_ReturnsFalse()
        {
            var registry = new ToolRegistry();
            registry.Register(new FakeTool("patch_range", ToolPermission.Write));
            var guard = new PermissionGuard(registry);

            var allowed = guard.IsAllowed("patch_range", AgentMode.Ask);

            Assert.False(allowed);
        }

        [Fact]
        public void IsAllowed_AgentModeWriteTool_ReturnsTrue()
        {
            var registry = new ToolRegistry();
            registry.Register(new FakeTool("patch_range", ToolPermission.Write));
            var guard = new PermissionGuard(registry);

            var allowed = guard.IsAllowed("patch_range", AgentMode.Agent);

            Assert.True(allowed);
        }

        [Fact]
        public void IsAllowed_UnregisteredTool_ReturnsFalse()
        {
            var registry = new ToolRegistry();
            var guard = new PermissionGuard(registry);

            var allowed = guard.IsAllowed("missing_tool", AgentMode.Ask);

            Assert.False(allowed);
        }

        private sealed class FakeTool : ITool
        {
            private readonly JsonElement _schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

            public FakeTool(string name, ToolPermission permission)
            {
                Name = name;
                RequiredPermission = permission;
            }

            public string Name { get; }

            public string Description => Name;

            public ToolPermission RequiredPermission { get; }

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
