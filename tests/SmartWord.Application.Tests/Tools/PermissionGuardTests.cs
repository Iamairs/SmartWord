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
        public void Decide_AgentReadOnlyModeWriteTools_ReturnsDenied()
        {
            var registry = CreateRegistryWithWriteTools();
            var guard = new PermissionGuard(registry);

            Assert.False(guard.Decide("patch_range", AgentMode.Agent, AgentPermissionMode.ReadOnly).IsAllowed);
            Assert.False(guard.Decide("execute_script", AgentMode.Agent, AgentPermissionMode.ReadOnly).IsAllowed);
            Assert.False(guard.Decide("todo_write", AgentMode.Agent, AgentPermissionMode.ReadOnly).IsAllowed);
        }

        [Fact]
        public void Decide_ConfirmWrites_RequiresConfirmationOnlyForDocumentWrites()
        {
            var registry = CreateRegistryWithWriteTools();
            var guard = new PermissionGuard(registry);

            var patch = guard.Decide("patch_range", AgentMode.Agent, AgentPermissionMode.ConfirmWrites);
            var script = guard.Decide("execute_script", AgentMode.Agent, AgentPermissionMode.ConfirmWrites);
            var todo = guard.Decide("todo_write", AgentMode.Agent, AgentPermissionMode.ConfirmWrites);

            Assert.True(patch.IsAllowed);
            Assert.True(patch.RequiresConfirmation);
            Assert.True(script.IsAllowed);
            Assert.True(script.RequiresConfirmation);
            Assert.True(todo.IsAllowed);
            Assert.False(todo.RequiresConfirmation);
        }

        [Fact]
        public void Decide_AutoSafeWrites_ConfirmsOnlyScriptWrites()
        {
            var registry = CreateRegistryWithWriteTools();
            var guard = new PermissionGuard(registry);

            var patch = guard.Decide("patch_range", AgentMode.Agent, AgentPermissionMode.AutoSafeWrites);
            var script = guard.Decide("execute_script", AgentMode.Agent, AgentPermissionMode.AutoSafeWrites);
            var localAutomation = guard.Decide("skill_run_script", AgentMode.Agent, AgentPermissionMode.AutoSafeWrites);
            var todo = guard.Decide("todo_write", AgentMode.Agent, AgentPermissionMode.AutoSafeWrites);

            Assert.True(patch.IsAllowed);
            Assert.False(patch.RequiresConfirmation);
            Assert.True(script.IsAllowed);
            Assert.True(script.RequiresConfirmation);
            Assert.True(localAutomation.IsAllowed);
            Assert.True(localAutomation.RequiresConfirmation);
            Assert.True(todo.IsAllowed);
            Assert.False(todo.RequiresConfirmation);
        }

        [Fact]
        public void Decide_FullAuto_AllowsWritesWithoutConfirmation()
        {
            var registry = CreateRegistryWithWriteTools();
            var guard = new PermissionGuard(registry);

            Assert.False(guard.Decide("patch_range", AgentMode.Agent, AgentPermissionMode.FullAuto).RequiresConfirmation);
            Assert.False(guard.Decide("execute_script", AgentMode.Agent, AgentPermissionMode.FullAuto).RequiresConfirmation);
            Assert.False(guard.Decide("todo_write", AgentMode.Agent, AgentPermissionMode.FullAuto).RequiresConfirmation);
        }

        [Fact]
        public void Decide_AskAndPlanWriteTools_ReturnDenied()
        {
            var registry = CreateRegistryWithWriteTools();
            var guard = new PermissionGuard(registry);

            Assert.False(guard.Decide("patch_range", AgentMode.Ask, AgentPermissionMode.FullAuto).IsAllowed);
            Assert.False(guard.Decide("execute_script", AgentMode.Plan, AgentPermissionMode.FullAuto).IsAllowed);
        }

        [Fact]
        public void IsAllowed_UnregisteredTool_ReturnsFalse()
        {
            var registry = new ToolRegistry();
            var guard = new PermissionGuard(registry);

            var allowed = guard.IsAllowed("missing_tool", AgentMode.Ask);

            Assert.False(allowed);
        }

        private static ToolRegistry CreateRegistryWithWriteTools()
        {
            var registry = new ToolRegistry();
            registry.Register(new FakeTool("patch_range", ToolPermission.DocumentPatchWrite));
            registry.Register(new FakeTool("execute_script", ToolPermission.ScriptWrite));
            registry.Register(new FakeTool("skill_run_script", ToolPermission.LocalAutomation));
            registry.Register(new FakeTool("todo_write", ToolPermission.StateWrite));
            return registry;
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

            public bool IsVisibleToModel => true;

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
