using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SmartWord.Application.Context;
using SmartWord.Application.PromptBuilder;
using SmartWord.Application.Todo;
using SmartWord.Application.Tools;
using SmartWord.Core.Enums;
using SmartWord.Core.Interfaces;
using SmartWord.Core.Models;
using SmartWord.Core.Telemetry;
using SmartWord.OfficeIntegration.Tools;

namespace SmartWord.Application.Orchestration
{
    /// <summary>
    /// Ask/Plan/Agent 共用的主编排循环，Phase 2 先完整支持 Ask 模式只读工具链路。
    /// </summary>
    public sealed partial class AgentOrchestrator : IAgentOrchestrator
    {
        private const int FixedIterationBudget = 100;
        private const int AskModeMaxIterations = FixedIterationBudget;
        private const int MaxToolCallsPerIteration = 20;
        private const int ConsecutiveFailureThreshold = 3;
        private const int WriteRepairAttemptLimit = 3;
        private static readonly TimeSpan ToolExecutionTimeout = TimeSpan.FromSeconds(30);
        private const int ToolErrorMessageMaxLength = 500;

        private readonly ILlmClient _llmClient;
        private readonly LlmTurnExecutor _llmTurnExecutor;
        private readonly IContextHydrator _contextHydrator;
        private readonly IConversationStore _conversationStore;
        private readonly SystemPromptBuilder _systemPromptBuilder;
        private readonly IToolRegistry _toolRegistry;
        private readonly ToolCallCoordinator _toolCallCoordinator;
        private readonly WriteStepCoordinator _writeStepCoordinator;
        private readonly IConfirmationChannel _confirmationChannel;
        private readonly PlanInterviewCoordinator _planInterviewCoordinator;
        private readonly IUndoScopeFactory _undoScopeFactory;
        private readonly ConversationCompressor _conversationCompressor;
        private readonly ContextCompactionService _contextCompactionService;
        private readonly TodoRunCoordinator _todoRunCoordinator;
        private readonly TodoReminderService _todoReminderService;
        private readonly ISkillPromptResolver _skillPromptResolver;
        private readonly RunAuditRecorder _runAuditRecorder;

        public AgentOrchestrator(
            ILlmClient llmClient,
            IContextHydrator contextHydrator,
            IConversationStore conversationStore,
            SystemPromptBuilder systemPromptBuilder,
            IToolRegistry toolRegistry,
            PermissionGuard permissionGuard,
            IConfirmationChannel confirmationChannel,
            IUndoScopeFactory undoScopeFactory,
            ConversationCompressor conversationCompressor,
            IQuestionChannel questionChannel = null,
            ITodoRecoveryChannel todoRecoveryChannel = null,
            TodoManager todoManager = null,
            TodoReminderService todoReminderService = null,
            ITaskHistoryStore taskHistoryStore = null,
            ISkillPromptResolver skillPromptResolver = null,
            ISkillScriptApprovalStore skillScriptApprovalStore = null,
            ContextCompactionService contextCompactionService = null,
            IAgentTelemetrySink telemetrySink = null)
        {
            _llmClient = llmClient;
            _llmTurnExecutor = new LlmTurnExecutor(llmClient);
            _contextHydrator = contextHydrator;
            _conversationStore = conversationStore;
            _systemPromptBuilder = systemPromptBuilder;
            _toolRegistry = toolRegistry;
            _toolCallCoordinator = new ToolCallCoordinator(
                toolRegistry,
                permissionGuard,
                skillScriptApprovalStore);
            _writeStepCoordinator = new WriteStepCoordinator(toolRegistry, conversationStore);
            _confirmationChannel = confirmationChannel;
            _planInterviewCoordinator = new PlanInterviewCoordinator(questionChannel);
            _undoScopeFactory = undoScopeFactory;
            _conversationCompressor = conversationCompressor ?? throw new ArgumentNullException(nameof(conversationCompressor));
            _contextCompactionService = contextCompactionService ?? new ContextCompactionService(
                llmClient,
                _conversationCompressor);
            _todoRunCoordinator = new TodoRunCoordinator(todoManager, todoRecoveryChannel);
            _todoReminderService = todoReminderService;
            _skillPromptResolver = skillPromptResolver;
            _runAuditRecorder = new RunAuditRecorder(taskHistoryStore, telemetrySink);
        }

    }
}
