using SmartWord.AddIn.UI.Themes;
using SmartWord.Core.Models.Conversation;
using SmartWord.Core.Orchestration.Conversation;
using SmartWord.Core.Abstractions;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartWord.AddIn.UI
{
    internal sealed class ChatPaneControl : UserControl
    {
        private readonly IConversationOrchestrator _conversationOrchestrator;
        private readonly INotificationService _notificationService;
        private readonly IThemeProvider _themeProvider;
        private readonly SplitContainer _splitContainer;

        private readonly ListBox _sessionListBox;
        private readonly Button _newSessionButton;
        private readonly RichTextBox _messageBox;
        private readonly TextBox _inputTextBox;
        private readonly Button _sendButton;
        private readonly Button _applyButton;
        private readonly Button _cancelActionButton;
        private readonly ComboBox _modelComboBox;
        private readonly TextBox _promptVersionTextBox;
        private readonly Label _statusLabel;

        private string _activeSessionId;
        private string _pendingActionId;
        private bool _isBusy;
        private bool _isRefreshingSessions;

        public ChatPaneControl(
            IConversationOrchestrator conversationOrchestrator,
            INotificationService notificationService,
            string[] availableModels,
            string defaultModel,
            string defaultPromptVersion)
        {
            _conversationOrchestrator = conversationOrchestrator;
            _notificationService = notificationService;
            _themeProvider = new LightThemeProvider();

            BackColor = _themeProvider.BackgroundColor;
            Font = _themeProvider.NormalFont;

            _splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 4,
                BackColor = _themeProvider.BorderColor,
                Panel1MinSize = 0,
                Panel2MinSize = 0
            };
            Controls.Add(_splitContainer);
            Resize += ChatPaneControl_Resize;

            var leftPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _themeProvider.SecondaryBackgroundColor,
                Padding = new Padding(8)
            };
            _splitContainer.Panel1.Controls.Add(leftPanel);

            _newSessionButton = new Button
            {
                Dock = DockStyle.Top,
                Height = 30,
                Text = "新建会话",
                BackColor = _themeProvider.BackgroundColor,
                FlatStyle = FlatStyle.Flat
            };
            _newSessionButton.FlatAppearance.BorderColor = _themeProvider.BorderColor;
            _newSessionButton.Click += NewSessionButton_Click;
            leftPanel.Controls.Add(_newSessionButton);

            _sessionListBox = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Top = 38,
                Font = _themeProvider.SmallFont
            };
            _sessionListBox.SelectedIndexChanged += SessionListBox_SelectedIndexChanged;
            leftPanel.Controls.Add(_sessionListBox);

            var rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = _themeProvider.BackgroundColor
            };
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
            _splitContainer.Panel2.Controls.Add(rightLayout);

            ApplySafeSplitterDistance();

            var topPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _themeProvider.SecondaryBackgroundColor,
                Padding = new Padding(8)
            };
            rightLayout.Controls.Add(topPanel, 0, 0);

            var modelLabel = new Label
            {
                Text = "模型",
                AutoSize = true,
                Left = 8,
                Top = 13,
                Font = _themeProvider.SmallFont,
                ForeColor = _themeProvider.TextColor
            };
            topPanel.Controls.Add(modelLabel);

            _modelComboBox = new ComboBox
            {
                Left = 48,
                Top = 9,
                Width = 150,
                DropDownStyle = ComboBoxStyle.DropDown
            };
            if (availableModels != null)
            {
                for (int i = 0; i < availableModels.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(availableModels[i]))
                    {
                        _modelComboBox.Items.Add(availableModels[i]);
                    }
                }
            }

            _modelComboBox.Text = string.IsNullOrWhiteSpace(defaultModel) ? string.Empty : defaultModel;
            topPanel.Controls.Add(_modelComboBox);

            var promptLabel = new Label
            {
                Text = "Prompt",
                AutoSize = true,
                Left = 208,
                Top = 13,
                Font = _themeProvider.SmallFont,
                ForeColor = _themeProvider.TextColor
            };
            topPanel.Controls.Add(promptLabel);

            _promptVersionTextBox = new TextBox
            {
                Left = 255,
                Top = 9,
                Width = 120,
                Text = defaultPromptVersion ?? string.Empty
            };
            topPanel.Controls.Add(_promptVersionTextBox);

            _messageBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = _themeProvider.BackgroundColor,
                ForeColor = _themeProvider.TextColor,
                Font = _themeProvider.NormalFont
            };
            rightLayout.Controls.Add(_messageBox, 0, 1);

            var bottomPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = _themeProvider.SecondaryBackgroundColor,
                Padding = new Padding(8)
            };
            rightLayout.Controls.Add(bottomPanel, 0, 2);

            _inputTextBox = new TextBox
            {
                Multiline = true,
                Left = 8,
                Top = 8,
                Width = 368,
                Height = 78,
                BorderStyle = BorderStyle.FixedSingle,
                Font = _themeProvider.NormalFont
            };
            _inputTextBox.KeyDown += InputTextBox_KeyDown;
            bottomPanel.Controls.Add(_inputTextBox);

            _sendButton = new Button
            {
                Text = "发送",
                Left = 384,
                Top = 8,
                Width = 72,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = _themeProvider.BackgroundColor
            };
            _sendButton.FlatAppearance.BorderColor = _themeProvider.BorderColor;
            _sendButton.Click += SendButton_Click;
            bottomPanel.Controls.Add(_sendButton);

            _applyButton = new Button
            {
                Text = "确认执行",
                Left = 384,
                Top = 42,
                Width = 72,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = _themeProvider.BackgroundColor,
                Enabled = false
            };
            _applyButton.FlatAppearance.BorderColor = _themeProvider.BorderColor;
            _applyButton.Click += ApplyButton_Click;
            bottomPanel.Controls.Add(_applyButton);

            _cancelActionButton = new Button
            {
                Text = "取消",
                Left = 384,
                Top = 76,
                Width = 72,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = _themeProvider.BackgroundColor,
                Enabled = false
            };
            _cancelActionButton.FlatAppearance.BorderColor = _themeProvider.BorderColor;
            _cancelActionButton.Click += CancelActionButton_Click;
            bottomPanel.Controls.Add(_cancelActionButton);

            _statusLabel = new Label
            {
                Left = 8,
                Top = 96,
                Width = 368,
                Height = 44,
                Font = _themeProvider.SmallFont,
                ForeColor = _themeProvider.TextColor,
                Text = "在此输入指令，系统会先给建议，再确认执行。"
            };
            bottomPanel.Controls.Add(_statusLabel);
        }

        public async Task InitializeAsync()
        {
            await LoadSessionsAsync().ConfigureAwait(true);
        }

        public void FocusInput()
        {
            _inputTextBox.Focus();
        }

        private async void NewSessionButton_Click(object sender, EventArgs e)
        {
            await RunSafeAsync(async () =>
            {
                ConversationSession session = await _conversationOrchestrator.CreateSessionAsync("新对话").ConfigureAwait(true);
                _activeSessionId = session == null ? string.Empty : session.SessionId;
                _pendingActionId = string.Empty;
                await LoadSessionsAsync().ConfigureAwait(true);
                _inputTextBox.Focus();
            }).ConfigureAwait(false);
        }

        private async void SessionListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_isRefreshingSessions)
            {
                return;
            }

            SessionListItem item = _sessionListBox.SelectedItem as SessionListItem;
            if (item == null || string.IsNullOrWhiteSpace(item.SessionId))
            {
                return;
            }

            await RunSafeAsync(async () =>
            {
                _activeSessionId = item.SessionId;
                await _conversationOrchestrator.SetActiveSessionAsync(item.SessionId).ConfigureAwait(true);
                await LoadSessionsAsync().ConfigureAwait(true);
            }).ConfigureAwait(false);
        }

        private async void SendButton_Click(object sender, EventArgs e)
        {
            await SubmitTurnAsync().ConfigureAwait(false);
        }

        private async Task SubmitTurnAsync()
        {
            string message = _inputTextBox.Text == null ? string.Empty : _inputTextBox.Text.Trim();
            if (message.Length == 0)
            {
                _statusLabel.Text = "请输入内容后再发送。";
                return;
            }

            await RunSafeAsync(async () =>
            {
                ChatTurnResult result = await _conversationOrchestrator.RunTurnAsync(new ChatTurnRequest
                {
                    SessionId = _activeSessionId,
                    UserMessage = message,
                    ModelOverride = _modelComboBox.Text,
                    PromptVersion = _promptVersionTextBox.Text
                }).ConfigureAwait(true);

                _inputTextBox.Clear();
                _activeSessionId = result == null ? _activeSessionId : result.SessionId;
                _pendingActionId = result == null ? string.Empty : result.PendingActionId;
                _applyButton.Enabled = result != null && result.RequiresUserConfirmation && !string.IsNullOrWhiteSpace(_pendingActionId);
                _cancelActionButton.Enabled = _applyButton.Enabled;

                if (result != null)
                {
                    _statusLabel.Text = result.RequiresUserConfirmation
                        ? "已生成建议，请点击“确认执行”。"
                        : "已返回结果。";
                }

                await LoadSessionsAsync().ConfigureAwait(true);
            }).ConfigureAwait(false);
        }

        private async void ApplyButton_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_activeSessionId) || string.IsNullOrWhiteSpace(_pendingActionId))
            {
                _statusLabel.Text = "当前没有待执行动作。";
                return;
            }

            await RunSafeAsync(async () =>
            {
                ApplyActionResult result = await _conversationOrchestrator
                    .ApplyPendingActionAsync(_activeSessionId, _pendingActionId)
                    .ConfigureAwait(true);

                _pendingActionId = string.Empty;
                _applyButton.Enabled = false;
                _cancelActionButton.Enabled = false;
                _statusLabel.Text = result == null ? "执行完成。" : result.Message;
                await LoadSessionsAsync().ConfigureAwait(true);
            }).ConfigureAwait(false);
        }

        private void CancelActionButton_Click(object sender, EventArgs e)
        {
            _pendingActionId = string.Empty;
            _applyButton.Enabled = false;
            _cancelActionButton.Enabled = false;
            _statusLabel.Text = "已取消待执行动作。";
        }

        private async void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await SubmitTurnAsync().ConfigureAwait(false);
            }
        }

        private async Task LoadSessionsAsync()
        {
            IReadOnlyList<ConversationSession> sessions = await _conversationOrchestrator.LoadSessionsAsync().ConfigureAwait(true);
            _isRefreshingSessions = true;
            try
            {
                _sessionListBox.BeginUpdate();
                _sessionListBox.Items.Clear();

                ConversationSession active = null;
                for (int i = 0; i < sessions.Count; i++)
                {
                    ConversationSession session = sessions[i];
                    var item = new SessionListItem(session.SessionId, session.Title, session.IsActive);
                    _sessionListBox.Items.Add(item);

                    if (active == null)
                    {
                        if (!string.IsNullOrWhiteSpace(_activeSessionId) && string.Equals(session.SessionId, _activeSessionId, StringComparison.OrdinalIgnoreCase))
                        {
                            active = session;
                        }
                        else if (session.IsActive)
                        {
                            active = session;
                        }
                    }
                }

                _sessionListBox.EndUpdate();

                if (active == null && sessions.Count > 0)
                {
                    active = sessions[0];
                }

                if (active != null)
                {
                    _activeSessionId = active.SessionId;
                    SelectSessionItem(_activeSessionId);
                    RenderMessages(active.Messages);
                }
                else
                {
                    _messageBox.Clear();
                }
            }
            finally
            {
                _isRefreshingSessions = false;
            }
        }

        private void SelectSessionItem(string sessionId)
        {
            for (int i = 0; i < _sessionListBox.Items.Count; i++)
            {
                SessionListItem item = _sessionListBox.Items[i] as SessionListItem;
                if (item != null && string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    _sessionListBox.SelectedIndex = i;
                    return;
                }
            }
        }

        private void RenderMessages(IList<ConversationMessage> messages)
        {
            var builder = new StringBuilder();
            if (messages != null)
            {
                for (int i = 0; i < messages.Count; i++)
                {
                    ConversationMessage message = messages[i];
                    string role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? "你" : "SmartWord";
                    builder.Append(role).Append(": ").AppendLine(message.Content ?? string.Empty).AppendLine();
                }
            }

            _messageBox.Text = builder.ToString();
            _messageBox.SelectionStart = _messageBox.Text.Length;
            _messageBox.ScrollToCaret();
        }

        private async Task RunSafeAsync(Func<Task> callback)
        {
            if (_isBusy)
            {
                return;
            }

            _isBusy = true;
            SetBusyState(true);
            try
            {
                await callback().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                string error = "操作失败：" + ex.Message;
                _statusLabel.Text = error;
                if (_notificationService != null)
                {
                    _notificationService.Error(error);
                }
            }
            finally
            {
                _isBusy = false;
                SetBusyState(false);
            }
        }

        private void SetBusyState(bool isBusy)
        {
            _sendButton.Enabled = !isBusy;
            _newSessionButton.Enabled = !isBusy;
            _sessionListBox.Enabled = !isBusy;
            _inputTextBox.Enabled = !isBusy;
            if (isBusy)
            {
                _applyButton.Enabled = false;
                _cancelActionButton.Enabled = false;
                _statusLabel.Text = "处理中，请稍候...";
            }
            else
            {
                bool hasPending = !string.IsNullOrWhiteSpace(_pendingActionId);
                _applyButton.Enabled = hasPending;
                _cancelActionButton.Enabled = hasPending;
            }
        }

        private void ChatPaneControl_Resize(object sender, EventArgs e)
        {
            ApplySafeSplitterDistance();
        }

        private void ApplySafeSplitterDistance()
        {
            if (_splitContainer == null || _splitContainer.Width <= 0)
            {
                return;
            }

            int width = _splitContainer.Width;
            if (width <= 1)
            {
                return;
            }

            const int desiredLeftWidth = 170;
            const int minLeftWidth = 120;
            const int minRightWidth = 220;

            int safeMin = Math.Max(1, Math.Min(minLeftWidth, width - 1));
            int safeMax = width - minRightWidth;

            int target;
            if (safeMax < safeMin)
            {
                // 极窄窗口下退化为中间分栏，避免触发 SplitterDistance 范围异常。
                target = width / 2;
            }
            else
            {
                target = Math.Max(safeMin, Math.Min(desiredLeftWidth, safeMax));
            }

            target = Math.Max(1, Math.Min(width - 1, target));
            if (target <= 0 || target >= width)
            {
                return;
            }

            _splitContainer.SplitterDistance = target;
        }

        private sealed class SessionListItem
        {
            public SessionListItem(string sessionId, string title, bool isActive)
            {
                SessionId = sessionId;
                Title = title;
                IsActive = isActive;
            }

            public string SessionId { get; private set; }

            public string Title { get; private set; }

            public bool IsActive { get; private set; }

            public override string ToString()
            {
                return (IsActive ? "* " : string.Empty) + (string.IsNullOrWhiteSpace(Title) ? "未命名会话" : Title);
            }
        }
    }
}
