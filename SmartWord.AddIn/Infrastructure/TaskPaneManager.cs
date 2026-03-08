using Microsoft.Office.Tools;
using SmartWord.AddIn.UI.Web;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Orchestration.Conversation;
using SmartWord.Services.Logging;
using System;

namespace SmartWord.AddIn.Infrastructure
{
    // 文件说明：
    // 封装 Word 任务侧栏（CustomTaskPane）的创建、显隐、焦点控制与生命周期释放逻辑。
    /// <summary>
    /// 对话侧栏管理器。
    /// 负责延迟创建聊天控件、同步 Ribbon 状态，并统一处理侧栏初始化异常。
    /// </summary>
    internal sealed class TaskPaneManager : IDisposable
    {
        private readonly ThisAddIn _addIn;
        private readonly IConversationOrchestrator _conversationOrchestrator;
        private readonly INotificationService _notificationService;
        private readonly string[] _availableModels;
        private readonly string _defaultModel;
        private readonly string _defaultPromptVersion;
        private readonly IAppLogger _logger;

        private WebChatPaneControl _chatPaneControl;
        private CustomTaskPane _chatPane;

        /// <summary>
        /// 初始化任务侧栏管理器。
        /// </summary>
        /// <param name="addIn">当前 AddIn 实例。</param>
        /// <param name="conversationOrchestrator">会话编排器。</param>
        /// <param name="notificationService">通知服务。</param>
        /// <param name="availableModels">可选模型列表。</param>
        /// <param name="defaultModel">默认模型。</param>
        /// <param name="defaultPromptVersion">默认 Prompt 版本。</param>
        /// <param name="logger">日志服务。</param>
        public TaskPaneManager(
            ThisAddIn addIn,
            IConversationOrchestrator conversationOrchestrator,
            INotificationService notificationService,
            string[] availableModels,
            string defaultModel,
            string defaultPromptVersion,
            IAppLogger logger)
        {
            _addIn = addIn;
            _conversationOrchestrator = conversationOrchestrator;
            _notificationService = notificationService;
            _availableModels = availableModels ?? new string[0];
            _defaultModel = defaultModel ?? string.Empty;
            _defaultPromptVersion = defaultPromptVersion ?? string.Empty;
            _logger = logger ?? NullAppLogger.Instance;
        }

        /// <summary>
        /// 当前侧栏是否可见。
        /// </summary>
        public bool IsVisible
        {
            get { return _chatPane != null && _chatPane.Visible; }
        }

        /// <summary>
        /// 切换侧栏显隐状态。
        /// </summary>
        public void Toggle()
        {
            SetVisible(!IsVisible);
        }

        /// <summary>
        /// 设置侧栏可见状态，并在可见时将输入焦点置于输入框。
        /// </summary>
        /// <param name="visible">目标显隐状态。</param>
        /// <returns>实际显隐状态。</returns>
        public bool SetVisible(bool visible)
        {
            EnsureCreated();
            if (_chatPane == null)
            {
                return false;
            }

            _chatPane.Visible = visible;
            _logger.Info("taskpane.set-visible", "Task pane visibility changed. Visible={Visible}", visible);
            if (visible && _chatPaneControl != null)
            {
                _chatPaneControl.FocusInput();
            }

            _addIn.NotifyChatPaneVisibilityChanged(_chatPane.Visible);
            return _chatPane.Visible;
        }

        /// <summary>
        /// 显示侧栏并聚焦输入框，常用于热键唤起。
        /// </summary>
        public void ShowAndFocus()
        {
            EnsureCreated();
            if (_chatPane != null)
            {
                _chatPane.Visible = true;
                _logger.Info("taskpane.show", "Task pane shown and focused.");
                if (_chatPaneControl != null)
                {
                    _chatPaneControl.FocusInput();
                }

                _addIn.NotifyChatPaneVisibilityChanged(true);
            }
        }

        /// <summary>
        /// 释放侧栏相关资源，解除事件订阅并销毁控件实例。
        /// </summary>
        public void Dispose()
        {
            if (_chatPane != null)
            {
                _chatPane.VisibleChanged -= ChatPane_VisibleChanged;
                _addIn.CustomTaskPanes.Remove(_chatPane);
                _chatPane = null;
                _logger.Debug("taskpane.dispose", "Task pane instance removed.");
            }

            if (_chatPaneControl != null)
            {
                _chatPaneControl.Dispose();
                _chatPaneControl = null;
            }
        }

        /// <summary>
        /// 按需创建聊天控件与 CustomTaskPane，避免 AddIn 启动阶段的 UI 开销。
        /// </summary>
        private void EnsureCreated()
        {
            if (_chatPane != null)
            {
                return;
            }

            _chatPaneControl = new WebChatPaneControl(
                _conversationOrchestrator,
                _notificationService,
                _availableModels,
                _defaultModel,
                _defaultPromptVersion,
                _logger);

            _chatPane = _addIn.CustomTaskPanes.Add(_chatPaneControl, "SmartWord 对话");
            _chatPane.DockPosition = Microsoft.Office.Core.MsoCTPDockPosition.msoCTPDockPositionRight;
            _chatPane.Width = 520;
            _chatPane.Visible = false;
            _chatPane.VisibleChanged += ChatPane_VisibleChanged;
            _logger.Info("taskpane.created", "Task pane created. Width={Width}", _chatPane.Width);

            // 异步加载历史会话，避免阻塞 UI 线程的首次展示。
            InitializePaneAsync();
        }

        /// <summary>
        /// 侧栏可见性变化时，回写 Ribbon 按钮状态。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void ChatPane_VisibleChanged(object sender, EventArgs e)
        {
            _addIn.NotifyChatPaneVisibilityChanged(IsVisible);
        }

        /// <summary>
        /// 异步初始化聊天面板，失败时统一走通知服务反馈。
        /// </summary>
        private async void InitializePaneAsync()
        {
            try
            {
                await _chatPaneControl.InitializeAsync().ConfigureAwait(true);
                _logger.Info("taskpane.initialize.success", "Task pane initialized successfully.");
            }
            catch (Exception ex)
            {
                _notificationService.Error("Chat pane initialization failed: " + ex.Message);
                _logger.Error("taskpane.initialize.failed", ex, "Task pane initialization failed.");
            }
        }
    }
}
