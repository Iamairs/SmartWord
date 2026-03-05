using SmartWord.AddIn.Infrastructure;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Core.Orchestration;
using SmartWord.Core.Orchestration.Conversation;
using SmartWord.Services.Conversation;
using SmartWord.Services.Embedding;
using SmartWord.Services.Model;
using SmartWord.Services.Orchestration;
using SmartWord.Services.Retrieval;
using SmartWord.Services.Routing;
using SmartWord.Services.Selection;
using SmartWord.Services.Storage;
using SmartWord.Services.Undo;
using SmartWord.Services.Vba;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SmartWord.AddIn
{
    public partial class ThisAddIn
    {
        private INotificationService _notificationService;
        private IUndoScopeFactory _undoScopeFactory;
        private IVbaExecutor _vbaExecutor;
        private IEditorAgentOrchestrator _editorAgentOrchestrator;
        private IVbaAgentOrchestrator _vbaAgentOrchestrator;
        private IConversationOrchestrator _conversationOrchestrator;
        private TaskPaneManager _taskPaneManager;
        private GlobalHotKeyManager _hotKeyManager;
        private bool _isOpeningPane;
        private OpenAiApiOptions _apiOptions;
        private string[] _availableModels = new string[0];
        private string _defaultModel = string.Empty;
        private string _defaultPromptVersion = string.Empty;

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _notificationService = new MessageBoxNotificationService();
            _undoScopeFactory = new WordUndoScopeFactory(Application, _notificationService);
            _vbaExecutor = new VbaExecutor(Application, _undoScopeFactory);

            var selectionService = new WordSelectionService(Application);
            _apiOptions = OpenAiApiOptions.LoadFromEnvironment(AppDomain.CurrentDomain.BaseDirectory);
            var modelService = CreateModelService(_apiOptions);
            var embeddingService = CreateEmbeddingService(_apiOptions);
            var sanitizer = new VbaCodeSanitizer();

            // 兼容保留旧链路，实现回退能力。
            _editorAgentOrchestrator = new EditorAgentOrchestrator(selectionService, modelService, _notificationService);
            _vbaAgentOrchestrator = new VbaAgentOrchestrator(selectionService, modelService, sanitizer, _vbaExecutor, _notificationService);

            var conversationStore = new FileConversationStore(_apiOptions.ChatStorePath);
            var chunkProvider = new WordDocumentChunkProvider(Application);
            var vectorIndexStore = new VectorIndexStore(_apiOptions.VectorIndexDirectory);
            var documentRetriever = new HybridDocumentRetriever(chunkProvider, embeddingService, vectorIndexStore, modelService);
            var routeService = new CommandRouteService(modelService);
            _conversationOrchestrator = new ConversationOrchestrator(
                conversationStore,
                documentRetriever,
                routeService,
                selectionService,
                modelService,
                sanitizer,
                _vbaExecutor,
                _notificationService);

            _availableModels = _apiOptions.AvailableModels ?? new string[0];
            _defaultModel = _apiOptions.Model ?? string.Empty;
            _defaultPromptVersion = _apiOptions.DefaultPromptVersion ?? string.Empty;

            _taskPaneManager = new TaskPaneManager(
                this,
                _conversationOrchestrator,
                _notificationService,
                _availableModels,
                _defaultModel,
                _defaultPromptVersion);

            try
            {
                _hotKeyManager = new GlobalHotKeyManager(() =>
                {
                    _ = HandleAltKHotKeyAsync();
                });
                _hotKeyManager.RegisterAltK();
            }
            catch (Exception ex)
            {
                _notificationService.Error("Hotkey registration failed: " + ex.Message);
            }
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            if (_hotKeyManager != null)
            {
                _hotKeyManager.Dispose();
                _hotKeyManager = null;
            }

            if (_taskPaneManager != null)
            {
                _taskPaneManager.Dispose();
                _taskPaneManager = null;
            }
        }

        private Task HandleAltKHotKeyAsync()
        {
            if (!IsWordForeground())
            {
                return Task.CompletedTask;
            }

            if (_isOpeningPane)
            {
                return Task.CompletedTask;
            }

            _isOpeningPane = true;
            try
            {
                if (Application == null)
                {
                    _notificationService.Error("Word application is not ready.");
                    return Task.CompletedTask;
                }

                _taskPaneManager.ShowAndFocus();
            }
            catch (Exception ex)
            {
                _notificationService.Error("Open chat pane failed: " + ex.Message);
            }
            finally
            {
                _isOpeningPane = false;
            }

            return Task.CompletedTask;
        }

        internal bool SetChatPaneVisible(bool visible)
        {
            if (_taskPaneManager == null)
            {
                return false;
            }

            return _taskPaneManager.SetVisible(visible);
        }

        internal void NotifyChatPaneVisibilityChanged(bool isVisible)
        {
            try
            {
                var ribbon = Globals.Ribbons.SmartWordRibbon;
                if (ribbon != null)
                {
                    ribbon.SyncPaneState(isVisible);
                }
            }
            catch
            {
                // Ribbon 在未初始化时忽略同步异常。
            }
        }

        private IModelService CreateModelService(OpenAiApiOptions options)
        {
            if (options.IsConfigured)
            {
                try
                {
                    return new OpenAiCompatibleModelService(options);
                }
                catch (Exception ex)
                {
                    _notificationService.Error("Failed to initialize OpenAI model service, fallback to local model: " + ex.Message);
                    return new LocalModelService();
                }
            }

            return new LocalModelService();
        }

        private IEmbeddingService CreateEmbeddingService(OpenAiApiOptions options)
        {
            if (options.IsConfigured)
            {
                try
                {
                    return new OpenAiEmbeddingService(options);
                }
                catch (Exception ex)
                {
                    _notificationService.Error("Failed to initialize OpenAI embedding service, fallback to local embedding: " + ex.Message);
                    return new LocalEmbeddingService();
                }
            }

            return new LocalEmbeddingService();
        }

        private bool IsWordForeground()
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                return false;
            }

            uint foregroundPid;
            GetWindowThreadProcessId(foregroundWindow, out foregroundPid);

            int currentPid = Process.GetCurrentProcess().Id;
            return foregroundPid == currentPid;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        #region VSTO generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InternalStartup()
        {
            Startup += new EventHandler(ThisAddIn_Startup);
            Shutdown += new EventHandler(ThisAddIn_Shutdown);
        }

        #endregion
    }
}

