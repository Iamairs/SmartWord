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
    // 文件说明：
    // VSTO 插件入口文件，负责 SmartWord 在 Word 宿主中的生命周期管理、依赖装配与 UI 管理器初始化。
    /// <summary>
    /// Word 插件主入口。
    /// 负责在启动时构建服务依赖，在关闭时释放资源，并协调 Ribbon 与聊天侧栏状态。
    /// </summary>
    public partial class ThisAddIn
    {
        // 以下字段在 AddIn 生命周期内复用，避免重复构建造成状态不一致。
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

        /// <summary>
        /// AddIn 启动入口：装配核心服务并初始化聊天侧栏与全局热键。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            // Step 1. 初始化基础能力：通知、撤销域与 VBA 执行器。
            _notificationService = new MessageBoxNotificationService();
            _undoScopeFactory = new WordUndoScopeFactory(Application, _notificationService);
            _vbaExecutor = new VbaExecutor(Application, _undoScopeFactory);

            // Step 2. 初始化模型与检索相关服务。
            var selectionService = new WordSelectionService(Application);
            _apiOptions = OpenAiApiOptions.LoadFromEnvironment(AppDomain.CurrentDomain.BaseDirectory);
            var modelService = CreateModelService(_apiOptions);
            var embeddingService = CreateEmbeddingService(_apiOptions);
            var sanitizer = new VbaCodeSanitizer();

            // 兼容保留旧链路，实现回退能力。
            _editorAgentOrchestrator = new EditorAgentOrchestrator(selectionService, modelService, _notificationService);
            _vbaAgentOrchestrator = new VbaAgentOrchestrator(selectionService, modelService, sanitizer, _vbaExecutor, _notificationService);

            // Step 3. 组装会话编排链路（会话存储 + 检索 + 路由 + 执行）。
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

            // Step 4. 初始化任务侧栏管理器（延迟创建实际控件，减少启动阻塞）。
            _taskPaneManager = new TaskPaneManager(
                this,
                _conversationOrchestrator,
                _notificationService,
                _availableModels,
                _defaultModel,
                _defaultPromptVersion);

            try
            {
                // Step 5. 注册 Alt+K 全局热键，提升对话入口唤起效率。
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

        /// <summary>
        /// AddIn 关闭入口：释放热键句柄与任务侧栏资源。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
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

        /// <summary>
        /// 处理 Alt+K 热键：仅在 Word 前台且未处于打开流程时显示并聚焦聊天侧栏。
        /// </summary>
        /// <returns>已完成任务。</returns>
        private Task HandleAltKHotKeyAsync()
        {
            // 仅在当前前台窗口属于 Word 进程时响应热键，避免抢占其他应用焦点。
            if (!IsWordForeground())
            {
                return Task.CompletedTask;
            }

            // 防止热键连击导致并发打开流程。
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

        /// <summary>
        /// 对外暴露侧栏显隐控制，供 Ribbon 按钮调用。
        /// </summary>
        /// <param name="visible">目标可见状态。</param>
        /// <returns>实际可见状态；当侧栏未初始化时返回 false。</returns>
        internal bool SetChatPaneVisible(bool visible)
        {
            if (_taskPaneManager == null)
            {
                return false;
            }

            return _taskPaneManager.SetVisible(visible);
        }

        /// <summary>
        /// 将侧栏可见状态同步到 Ribbon，确保按钮选中态与真实状态一致。
        /// </summary>
        /// <param name="isVisible">侧栏是否可见。</param>
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

        /// <summary>
        /// 按配置创建模型服务；当远端模型初始化失败时回退到本地模型。
        /// </summary>
        /// <param name="options">模型配置。</param>
        /// <returns>可用的模型服务实例。</returns>
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

        /// <summary>
        /// 按配置创建向量化服务；当远端嵌入初始化失败时回退到本地实现。
        /// </summary>
        /// <param name="options">模型配置。</param>
        /// <returns>可用的向量化服务实例。</returns>
        private IEmbeddingService CreateEmbeddingService(OpenAiApiOptions options)
        {
            if (options.IsEmbeddingConfigured)
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

        /// <summary>
        /// 判断当前前台窗口是否属于本进程（Word 插件宿主）。
        /// </summary>
        /// <returns>为当前 Word 进程前台时返回 true，否则返回 false。</returns>
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

