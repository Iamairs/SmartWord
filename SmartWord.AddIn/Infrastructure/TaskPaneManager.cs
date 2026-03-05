using SmartWord.AddIn.UI;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Orchestration.Conversation;
using Microsoft.Office.Tools;
using System;

namespace SmartWord.AddIn.Infrastructure
{
    internal sealed class TaskPaneManager : IDisposable
    {
        private readonly ThisAddIn _addIn;
        private readonly IConversationOrchestrator _conversationOrchestrator;
        private readonly INotificationService _notificationService;
        private readonly string[] _availableModels;
        private readonly string _defaultModel;
        private readonly string _defaultPromptVersion;

        private ChatPaneControl _chatPaneControl;
        private CustomTaskPane _chatPane;

        public TaskPaneManager(
            ThisAddIn addIn,
            IConversationOrchestrator conversationOrchestrator,
            INotificationService notificationService,
            string[] availableModels,
            string defaultModel,
            string defaultPromptVersion)
        {
            _addIn = addIn;
            _conversationOrchestrator = conversationOrchestrator;
            _notificationService = notificationService;
            _availableModels = availableModels ?? new string[0];
            _defaultModel = defaultModel ?? string.Empty;
            _defaultPromptVersion = defaultPromptVersion ?? string.Empty;
        }

        public bool IsVisible
        {
            get { return _chatPane != null && _chatPane.Visible; }
        }

        public void Toggle()
        {
            SetVisible(!IsVisible);
        }

        public bool SetVisible(bool visible)
        {
            EnsureCreated();
            if (_chatPane == null)
            {
                return false;
            }

            _chatPane.Visible = visible;
            if (visible && _chatPaneControl != null)
            {
                _chatPaneControl.FocusInput();
            }

            _addIn.NotifyChatPaneVisibilityChanged(_chatPane.Visible);
            return _chatPane.Visible;
        }

        public void ShowAndFocus()
        {
            EnsureCreated();
            if (_chatPane != null)
            {
                _chatPane.Visible = true;
                if (_chatPaneControl != null)
                {
                    _chatPaneControl.FocusInput();
                }

                _addIn.NotifyChatPaneVisibilityChanged(true);
            }
        }

        public void Dispose()
        {
            if (_chatPane != null)
            {
                _chatPane.VisibleChanged -= ChatPane_VisibleChanged;
                _addIn.CustomTaskPanes.Remove(_chatPane);
                _chatPane = null;
            }

            if (_chatPaneControl != null)
            {
                _chatPaneControl.Dispose();
                _chatPaneControl = null;
            }
        }

        private void EnsureCreated()
        {
            if (_chatPane != null)
            {
                return;
            }

            _chatPaneControl = new ChatPaneControl(
                _conversationOrchestrator,
                _notificationService,
                _availableModels,
                _defaultModel,
                _defaultPromptVersion);

            _chatPane = _addIn.CustomTaskPanes.Add(_chatPaneControl, "SmartWord 对话");
            _chatPane.DockPosition = Microsoft.Office.Core.MsoCTPDockPosition.msoCTPDockPositionRight;
            _chatPane.Width = 520;
            _chatPane.Visible = false;
            _chatPane.VisibleChanged += ChatPane_VisibleChanged;

            InitializePaneAsync();
        }

        private void ChatPane_VisibleChanged(object sender, EventArgs e)
        {
            _addIn.NotifyChatPaneVisibilityChanged(IsVisible);
        }

        private async void InitializePaneAsync()
        {
            try
            {
                await _chatPaneControl.InitializeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _notificationService.Error("Chat pane initialization failed: " + ex.Message);
            }
        }
    }
}
