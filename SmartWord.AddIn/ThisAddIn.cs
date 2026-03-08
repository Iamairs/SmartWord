using SmartWord.AddIn.Infrastructure;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Abstractions.Conversation;
using SmartWord.Core.Orchestration;
using SmartWord.Core.Orchestration.Conversation;
using SmartWord.Services.Conversation;
using SmartWord.Services.Embedding;
using SmartWord.Services.Logging;
using SmartWord.Services.Model;
using SmartWord.Services.Orchestration;
using SmartWord.Services.Retrieval;
using SmartWord.Services.Routing;
using SmartWord.Services.Selection;
using SmartWord.Services.Storage;
using SmartWord.Services.Threading;
using SmartWord.Services.Undo;
using SmartWord.Services.Vba;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinFormsApplication = System.Windows.Forms.Application;

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
        private INotificationService _notificationService;  // 通知服务：提供用户提示接口，当前实现基于 MessageBox。
        private IAppLogger _logger = NullAppLogger.Instance; // 日志服务：统一记录结构化日志，支持关键链路追踪。
        private IUndoScopeFactory _undoScopeFactory;        // 撤销作用域工厂：提供撤销范围创建接口，当前实现基于 Word UndoRecord。
        private IVbaExecutor _vbaExecutor;                  // VBA 执行器：提供 VBA 代码执行接口，当前实现通过临时模块注入方式运行 VBA 代码。
        private IEditorAgentOrchestrator _editorAgentOrchestrator;      // 文本改写编排器：负责串联选区读取、模型改写、结果替换与用户通知。
        private IVbaAgentOrchestrator _vbaAgentOrchestrator;            // VBA 编排器：负责串联选区读取、模型生成、VBA 代码执行与用户通知。
        private IConversationOrchestrator _conversationOrchestrator;    // 会话编排器：负责串联会话存储、文档检索、命令路由与执行反馈，支持复杂对话场景。
        private TaskPaneManager _taskPaneManager;           // 任务侧栏管理器：负责聊天侧栏的创建、显隐控制、焦点管理与资源释放。
        private GlobalHotKeyManager _hotKeyManager;         // 全局热键管理器：负责 Alt+K 热键的注册、事件响应与资源释放。
        private bool _isOpeningPane;                        // 侧栏打开状态标志：防止热键连击导致的并发打开流程。
        private OpenAiApiOptions _apiOptions;
        private string[] _availableModels = new string[0];
        private string _defaultModel = string.Empty;
        private string _defaultPromptVersion = string.Empty;

        private UnhandledExceptionEventHandler _unhandledExceptionHandler;
        private EventHandler<UnobservedTaskExceptionEventArgs> _unobservedTaskExceptionHandler;
        private ThreadExceptionEventHandler _threadExceptionHandler;

        /// <summary>
        /// AddIn 启动入口：装配核心服务并初始化聊天侧栏与全局热键。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _notificationService = new MessageBoxNotificationService();

            try
            {
                _apiOptions = OpenAiApiOptions.LoadFromEnvironment(AppDomain.CurrentDomain.BaseDirectory);
                _logger = LoggingBootstrapper.Initialize(_apiOptions.Logging);
                RegisterGlobalExceptionLogging();
                _logger.Info("app.start", "SmartWord startup. BaseDirectory={BaseDirectory}", AppDomain.CurrentDomain.BaseDirectory);
                // 在启动线程捕获同步上下文，用于后续将 COM 访问统一封送回 Word 主线程。
                IWordThreadInvoker wordThreadInvoker = new WordThreadInvoker(SynchronizationContext.Current, Thread.CurrentThread.ManagedThreadId);

                // Step 1. 初始化基础能力：通知、撤销域与 VBA 执行器。
                _undoScopeFactory = new WordUndoScopeFactory(Application, _notificationService, wordThreadInvoker);
                _vbaExecutor = new VbaExecutor(Application, _undoScopeFactory, _logger, wordThreadInvoker);

                // Step 2. 初始化模型与检索相关服务。
                var selectionService = new WordSelectionService(Application, wordThreadInvoker);   // 选区服务：提供当前选区文本获取接口
                var modelService = CreateModelService(_apiOptions);
                var embeddingService = CreateEmbeddingService(_apiOptions);
                var sanitizer = new VbaCodeSanitizer();

                // 兼容保留旧链路，实现回退能力。
                _editorAgentOrchestrator = new EditorAgentOrchestrator(selectionService, modelService, _notificationService);
                _vbaAgentOrchestrator = new VbaAgentOrchestrator(selectionService, modelService, sanitizer, _vbaExecutor, _notificationService);

                // Step 3. 组装会话编排链路（会话存储 + 检索 + 路由 + 执行）。
                var conversationStore = new FileConversationStore(_apiOptions.ChatStorePath, _logger);
                var chunkProvider = new WordDocumentChunkProvider(Application, wordThreadInvoker);
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
                    _notificationService,
                    _logger);

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
                    _defaultPromptVersion,
                    _logger);

                try
                {
                    // Step 5. 注册 Alt+K 全局热键，提升对话入口唤起效率。
                    _hotKeyManager = new GlobalHotKeyManager(() =>
                    {
                        _ = HandleAltKHotKeyAsync();
                    });
                    _hotKeyManager.RegisterAltK();
                    _logger.Info("hotkey.register", "Alt+K hotkey registered successfully.");
                }
                catch (Exception ex)
                {
                    _notificationService.Error("Hotkey registration failed: " + ex.Message);
                    _logger.Error("hotkey.register.failed", ex, "Alt+K hotkey registration failed.");
                }
            }
            catch (Exception ex)
            {
                _notificationService.Error("SmartWord startup failed: " + ex.Message);
                _logger.Error("app.start.failed", ex, "SmartWord startup failed.");
            }
        }

        /// <summary>
        /// AddIn 关闭入口：释放热键句柄与任务侧栏资源。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            _logger.Info("app.shutdown", "SmartWord shutdown started.");
            UnregisterGlobalExceptionLogging();

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

            _logger.Info("app.shutdown", "SmartWord shutdown completed.");
            LoggingBootstrapper.Shutdown();
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
                _logger.Debug("hotkey.ignored", "Alt+K ignored because Word is not foreground.");
                return Task.CompletedTask;
            }

            // 防止热键连击导致并发打开流程。
            if (_isOpeningPane)
            {
                _logger.Debug("hotkey.ignored", "Alt+K ignored because pane is opening.");
                return Task.CompletedTask;
            }

            _isOpeningPane = true;
            try
            {
                if (Application == null)
                {
                    _notificationService.Error("Word application is not ready.");
                    _logger.Warn("hotkey.failed", "Alt+K failed because Word application is not ready.");
                    return Task.CompletedTask;
                }

                _taskPaneManager.ShowAndFocus();
                _logger.Info("hotkey.triggered", "Alt+K handled and task pane opened.");
            }
            catch (Exception ex)
            {
                _notificationService.Error("Open chat pane failed: " + ex.Message);
                _logger.Error("hotkey.failed", ex, "Open chat pane failed when handling Alt+K.");
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
                _logger.Warn("taskpane.not-ready", "Task pane is not initialized. Visible={Visible}", visible);
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
            catch (Exception ex)
            {
                // Ribbon 在未初始化时忽略同步异常。
                _logger.Debug("ribbon.sync.failed", "Ribbon sync failed. Visible={Visible} Error={Error}", isVisible, ex.Message);
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
                    _logger.Info("model.init", "Initializing remote model service. Model={Model}", options.Model);
                    return new OpenAiCompatibleModelService(options, _logger);
                }
                catch (Exception ex)
                {
                    _notificationService.Error("Failed to initialize OpenAI model service, fallback to local model: " + ex.Message);
                    _logger.Error("model.init.failed", ex, "Remote model initialization failed. Falling back to local model.");
                    return new LocalModelService();
                }
            }

            _logger.Warn("model.init.local", "Remote model is not configured. Using local model service.");
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
                    _logger.Info("embedding.init", "Initializing remote embedding service. Model={Model}", options.EmbeddingModel);
                    return new OpenAiEmbeddingService(options, _logger);
                }
                catch (Exception ex)
                {
                    _notificationService.Error("Failed to initialize OpenAI embedding service, fallback to local embedding: " + ex.Message);
                    _logger.Error("embedding.init.failed", ex, "Remote embedding initialization failed. Falling back to local embedding.");
                    return new LocalEmbeddingService();
                }
            }

            _logger.Warn("embedding.init.local", "Remote embedding is not configured. Using local embedding service.");
            return new LocalEmbeddingService();
        }

        /// <summary>
        /// 注册全局异常日志，确保未捕获异常可回溯。
        /// </summary>
        private void RegisterGlobalExceptionLogging()
        {
            if (_unhandledExceptionHandler == null)
            {
                _unhandledExceptionHandler = (sender, args) =>
                {
                    Exception ex = args.ExceptionObject as Exception;
                    _logger.Fatal("app.unhandled", ex, "Unhandled exception captured. IsTerminating={IsTerminating}", args.IsTerminating);
                };
                AppDomain.CurrentDomain.UnhandledException += _unhandledExceptionHandler;
            }

            if (_unobservedTaskExceptionHandler == null)
            {
                _unobservedTaskExceptionHandler = (sender, args) =>
                {
                    _logger.Error("task.unobserved", args.Exception, "Unobserved task exception captured.");
                    args.SetObserved();
                };
                TaskScheduler.UnobservedTaskException += _unobservedTaskExceptionHandler;
            }

            if (_threadExceptionHandler == null)
            {
                _threadExceptionHandler = (sender, args) =>
                {
                    _logger.Error("ui.thread.exception", args.Exception, "UI thread exception captured.");
                };
                WinFormsApplication.ThreadException += _threadExceptionHandler;
            }
        }

        /// <summary>
        /// 注销全局异常日志，避免重复注册。
        /// </summary>
        private void UnregisterGlobalExceptionLogging()
        {
            if (_unhandledExceptionHandler != null)
            {
                AppDomain.CurrentDomain.UnhandledException -= _unhandledExceptionHandler;
                _unhandledExceptionHandler = null;
            }

            if (_unobservedTaskExceptionHandler != null)
            {
                TaskScheduler.UnobservedTaskException -= _unobservedTaskExceptionHandler;
                _unobservedTaskExceptionHandler = null;
            }

            if (_threadExceptionHandler != null)
            {
                WinFormsApplication.ThreadException -= _threadExceptionHandler;
                _threadExceptionHandler = null;
            }
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
