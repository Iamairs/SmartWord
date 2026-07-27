using SmartWord.Application.Context;
using SmartWord.Application.Orchestration;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Todo;
using SmartWord.Application.Tools;
using SmartWord.Core.Interfaces;
using SmartWord.Infrastructure.Persistence;
using SmartWord.OfficeIntegration.Scripting;
using SmartWord.OfficeIntegration.Tools;
using SmartWord.OfficeIntegration.WordWrappers;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    internal static class ToolTestFactory
    {
        public static ToolRegistry CreateRegistry(WordApplicationWrapper wordWrapper)
        {
            var validator = new ScriptSecurityValidator();
            var executor = new CSharpScriptExecutor();
            var registry = new ToolRegistry();
            registry.Register(new ProbeDocumentTool(wordWrapper));
            registry.Register(new ReadSectionTool(wordWrapper));
            registry.Register(new GrepDocumentTool(wordWrapper));
            registry.Register(new GetSelectionContextTool(wordWrapper));
            registry.Register(new ReadTableTool(wordWrapper));
            registry.Register(new ReadAnnotationsTool(wordWrapper));
            registry.Register(new ReadScriptTool(wordWrapper, executor, validator));
            registry.Register(new VerifyScriptTool(wordWrapper, executor, validator));
            registry.Register(new PatchRangeTool(wordWrapper));
            registry.Register(new ExecuteScriptTool(wordWrapper, executor, validator));
            registry.Register(new AskUserQuestionTool());
            return registry;
        }

        public static AgentOrchestrator CreateOrchestrator(
            WordApplicationWrapper wordWrapper,
            ILlmClient llmClient,
            IConfirmationChannel confirmationChannel = null)
        {
            var registry = CreateRegistry(wordWrapper);
            var todoManager = new TodoManager(new InMemoryTodoStore());
            return new AgentOrchestrator(
                llmClient,
                new ContextHydrator(wordWrapper),
                new InMemoryConversationStore(),
                new SystemPromptBuilder(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Resources", "Prompts")),
                registry,
                new PermissionGuard(registry),
                confirmationChannel ?? new AlwaysConfirmChannel(),
                wordWrapper,
                new ConversationCompressor(),
                null,
                null,
                todoManager,
                new TodoReminderService());
        }
    }
}
