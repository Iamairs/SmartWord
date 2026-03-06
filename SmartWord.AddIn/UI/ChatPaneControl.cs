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
    // 文件说明：
    // 聊天侧栏核心控件，负责会话列表管理、对话交互、待执行动作确认以及 UI 状态控制。
    /// <summary>
    /// SmartWord 对话侧栏控件。
    /// 将编排器能力映射为可交互 UI，并在 WinForms 环境下维护统一的忙碌态与异常提示机制。
    /// </summary>
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
        private readonly int _uiThreadId;

        private string _activeSessionId;
        private string _pendingActionId;
        private bool _isBusy;
        private bool _isRefreshingSessions;

        /// <summary>
        /// 初始化聊天侧栏控件并构建界面元素。
        /// </summary>
        /// <param name="conversationOrchestrator">会话编排器。</param>
        /// <param name="notificationService">通知服务。</param>
        /// <param name="availableModels">可选模型列表。</param>
        /// <param name="defaultModel">默认模型。</param>
        /// <param name="defaultPromptVersion">默认 Prompt 版本。</param>
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
            _uiThreadId = Environment.CurrentManagedThreadId;

            // 统一设置主题基础属性，保证控件观感一致。
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

            // 初始化分栏宽度，避免首次渲染时出现越界异常。
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

        /// <summary>
        /// 异步初始化控件数据（加载会话列表及历史消息）。
        /// </summary>
        public async Task InitializeAsync()
        {
            await LoadSessionsAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 将焦点定位到输入框，便于用户直接输入指令。
        /// </summary>
        public void FocusInput()
        {
            if (IsDisposed)
            {
                return;
            }

            if (Environment.CurrentManagedThreadId == _uiThreadId)
            {
                _inputTextBox.Focus();
                return;
            }

            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed)
                    {
                        _inputTextBox.Focus();
                    }
                }));
            }
        }

        /// <summary>
        /// 新建会话按钮事件：创建会话并刷新左侧会话列表。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private async void NewSessionButton_Click(object sender, EventArgs e)
        {
            await RunSafeAsync(async () =>
            {
                ConversationSession session = await _conversationOrchestrator.CreateSessionAsync("新对话").ConfigureAwait(false);
                await RunOnUiThreadAsync(() =>
                {
                    _activeSessionId = session == null ? string.Empty : session.SessionId;
                    _pendingActionId = string.Empty;
                }).ConfigureAwait(false);

                await LoadSessionsAsync().ConfigureAwait(false);
                await RunOnUiThreadAsync(() => _inputTextBox.Focus()).ConfigureAwait(false);
            });
        }

        /// <summary>
        /// 会话列表选中事件：切换当前活跃会话并刷新消息区。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private async void SessionListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            // 刷新列表时会触发选中变化事件，这里直接忽略以避免重复请求。
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
                await _conversationOrchestrator.SetActiveSessionAsync(item.SessionId).ConfigureAwait(false);
                await LoadSessionsAsync().ConfigureAwait(false);
            });
        }

        /// <summary>
        /// 发送按钮事件：提交当前输入为一次新对话轮次。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private async void SendButton_Click(object sender, EventArgs e)
        {
            await SubmitTurnAsync();
        }

        /// <summary>
        /// 提交当前输入并处理编排结果（更新会话、待执行动作与状态提示）。
        /// </summary>
        private async Task SubmitTurnAsync()
        {
            string message = string.Empty;
            string modelOverride = string.Empty;
            string promptVersion = string.Empty;
            string activeSessionId = string.Empty;
            await RunOnUiThreadAsync(() =>
            {
                message = _inputTextBox.Text == null ? string.Empty : _inputTextBox.Text.Trim();
                modelOverride = _modelComboBox.Text;
                promptVersion = _promptVersionTextBox.Text;
                activeSessionId = _activeSessionId;
            }).ConfigureAwait(false);

            if (message.Length == 0)
            {
                await RunOnUiThreadAsync(() =>
                {
                    _statusLabel.Text = "请输入内容后再发送。";
                }).ConfigureAwait(false);
                return;
            }

            await RunSafeAsync(async () =>
            {
                // 每一轮对话都允许用户临时覆盖模型与 Prompt 版本，便于快速试验。
                ChatTurnResult result = await _conversationOrchestrator.RunTurnAsync(new ChatTurnRequest
                {
                    SessionId = activeSessionId,
                    UserMessage = message,
                    ModelOverride = modelOverride,
                    PromptVersion = promptVersion
                }).ConfigureAwait(false);

                await RunOnUiThreadAsync(() =>
                {
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
                }).ConfigureAwait(false);

                await LoadSessionsAsync().ConfigureAwait(false);
            });
        }

        /// <summary>
        /// 确认执行按钮事件：应用当前待执行动作。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private async void ApplyButton_Click(object sender, EventArgs e)
        {
            string activeSessionId = _activeSessionId;
            string pendingActionId = _pendingActionId;
            if (string.IsNullOrWhiteSpace(activeSessionId) || string.IsNullOrWhiteSpace(pendingActionId))
            {
                await RunOnUiThreadAsync(() =>
                {
                    _statusLabel.Text = "当前没有待执行动作。";
                }).ConfigureAwait(false);
                return;
            }

            await RunSafeAsync(async () =>
            {
                ApplyActionResult result = await _conversationOrchestrator
                    .ApplyPendingActionAsync(activeSessionId, pendingActionId)
                    .ConfigureAwait(false);

                await RunOnUiThreadAsync(() =>
                {
                    _pendingActionId = string.Empty;
                    _applyButton.Enabled = false;
                    _cancelActionButton.Enabled = false;
                    _statusLabel.Text = result == null ? "执行完成。" : result.Message;
                }).ConfigureAwait(false);
                await LoadSessionsAsync().ConfigureAwait(false);
            });
        }

        /// <summary>
        /// 取消按钮事件：清空当前待执行动作并恢复按钮状态。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void CancelActionButton_Click(object sender, EventArgs e)
        {
            _pendingActionId = string.Empty;
            _applyButton.Enabled = false;
            _cancelActionButton.Enabled = false;
            _statusLabel.Text = "已取消待执行动作。";
        }

        /// <summary>
        /// 输入框键盘事件：按 Enter（不带 Shift）时触发发送。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">键盘事件参数。</param>
        private async void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                await SubmitTurnAsync();
            }
        }

        /// <summary>
        /// 加载会话列表并渲染活跃会话消息。
        /// </summary>
        private async Task LoadSessionsAsync()
        {
            IReadOnlyList<ConversationSession> sessions = await _conversationOrchestrator.LoadSessionsAsync().ConfigureAwait(false);

            await RunOnUiThreadAsync(() =>
            {
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
                        // 未找到匹配活跃会话时，默认展示第一条，保证 UI 有稳定落点。
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
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 根据会话 ID 在列表中选中对应项。
        /// </summary>
        /// <param name="sessionId">目标会话 ID。</param>
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

        /// <summary>
        /// 将会话消息序列渲染为可读文本并滚动到末尾。
        /// </summary>
        /// <param name="messages">会话消息集合。</param>
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

        /// <summary>
        /// 统一异步执行保护：防重入、异常兜底与忙碌态切换。
        /// </summary>
        /// <param name="callback">实际执行的异步逻辑。</param>
        private async Task RunSafeAsync(Func<Task> callback)
        {
            bool canExecute = false;
            await RunOnUiThreadAsync(() =>
            {
                if (_isBusy)
                {
                    canExecute = false;
                    return;
                }

                _isBusy = true;
                SetBusyState(true);
                canExecute = true;
            });

            if (!canExecute)
            {
                return;
            }

            try
            {
                await callback().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // UI 层统一收口异常，避免事件处理链中断。
                string error = "操作失败：" + ex.Message;
                await RunOnUiThreadAsync(() =>
                {
                    _statusLabel.Text = error;
                    if (_notificationService != null)
                    {
                        _notificationService.Error(error);
                    }
                });
            }
            finally
            {
                await RunOnUiThreadAsync(() =>
                {
                    _isBusy = false;
                    SetBusyState(false);
                });
            }
        }

        /// <summary>
        /// 将指定逻辑封送到 UI 线程执行，避免跨线程访问 WinForms 控件。
        /// </summary>
        /// <param name="action">需要在 UI 线程执行的同步逻辑。</param>
        /// <returns>逻辑执行完成后返回已完成任务。</returns>
        private Task RunOnUiThreadAsync(Action action)
        {
            if (action == null || IsDisposed)
            {
                return Task.CompletedTask;
            }

            if (Environment.CurrentManagedThreadId == _uiThreadId)
            {
                action();
                return Task.CompletedTask;
            }

            if (!IsHandleCreated)
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>();
            BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!IsDisposed)
                    {
                        action();
                    }

                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }));
            return tcs.Task;
        }

        /// <summary>
        /// 根据忙碌状态批量更新控件可用性，防止用户在处理中重复触发动作。
        /// </summary>
        /// <param name="isBusy">是否处于忙碌状态。</param>
        private void SetBusyState(bool isBusy)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action<bool>(SetBusyState), isBusy);
                }

                return;
            }

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

        /// <summary>
        /// 容器尺寸变化事件：重新计算分栏比例。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void ChatPaneControl_Resize(object sender, EventArgs e)
        {
            ApplySafeSplitterDistance();
        }

        /// <summary>
        /// 计算并应用安全的分栏距离，规避极窄窗口下的越界异常。
        /// </summary>
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

        /// <summary>
        /// 会话列表显示项。
        /// </summary>
        private sealed class SessionListItem
        {
            /// <summary>
            /// 初始化会话列表项。
            /// </summary>
            /// <param name="sessionId">会话 ID。</param>
            /// <param name="title">会话标题。</param>
            /// <param name="isActive">是否为活跃会话。</param>
            public SessionListItem(string sessionId, string title, bool isActive)
            {
                SessionId = sessionId;
                Title = title;
                IsActive = isActive;
            }

            public string SessionId { get; private set; }

            public string Title { get; private set; }

            public bool IsActive { get; private set; }

            /// <summary>
            /// 返回列表显示文本；活跃会话前缀 <c>*</c> 便于快速识别。
            /// </summary>
            /// <returns>用于 ListBox 展示的文本。</returns>
            public override string ToString()
            {
                return (IsActive ? "* " : string.Empty) + (string.IsNullOrWhiteSpace(Title) ? "未命名会话" : Title);
            }
        }
    }
}
