using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Orchestration.Conversation;
using SmartWord.Services.Logging;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartWord.AddIn.UI.Web
{
    // 文件说明：
    // WebView2 聊天容器控件，负责加载 Vue 前端资源并桥接 C# 后端能力。
    internal sealed class WebChatPaneControl : UserControl
    {
        private readonly INotificationService _notificationService;
        private readonly IAppLogger _logger;
        private readonly WebViewRpcBridge _rpcBridge;
        private readonly WebView2 _webView;
        private readonly Label _errorLabel;
        private readonly int _uiThreadId;

        private bool _isFailed;
        private bool _eventsBound;
        private Task _initializeTask;

        /// <summary>
        /// 初始化 Web 聊天控件。
        /// </summary>
        public WebChatPaneControl(
            IConversationOrchestrator conversationOrchestrator,
            ISelectionService selectionService,
            INotificationService notificationService,
            string[] availableModels,
            string defaultModel,
            string defaultPromptVersion,
            int defaultBm25CandidateCount,
            int defaultDenseCandidateCount,
            int defaultRerankCandidateCount,
            int defaultMaxContextCharacters,
            int defaultNeighborWindow,
            IAppLogger logger)
        {
            _notificationService = notificationService;
            _logger = logger ?? NullAppLogger.Instance;
            _uiThreadId = Environment.CurrentManagedThreadId;
            _rpcBridge = new WebViewRpcBridge(
                conversationOrchestrator,
                selectionService,
                availableModels,
                defaultModel,
                defaultPromptVersion,
                defaultBm25CandidateCount,
                defaultDenseCandidateCount,
                defaultRerankCandidateCount,
                defaultMaxContextCharacters,
                defaultNeighborWindow,
                _logger);

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Visible = true
            };
            Controls.Add(_webView);

            _errorLabel = new Label
            {
                Dock = DockStyle.Fill,
                Visible = false,
                AutoEllipsis = false,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new System.Drawing.Font("Microsoft YaHei UI", 10f),
                ForeColor = System.Drawing.Color.FromArgb(180, 40, 40),
                BackColor = System.Drawing.Color.White,
                Padding = new Padding(20)
            };
            Controls.Add(_errorLabel);
        }

        /// <summary>
        /// 异步初始化 WebView2 与前端页面。
        /// </summary>
        public Task InitializeAsync()
        {
            if (_initializeTask != null)
            {
                return _initializeTask;
            }

            _initializeTask = InitializeCoreAsync();
            return _initializeTask;
        }

        /// <summary>
        /// 将输入焦点定位到前端输入框。
        /// </summary>
        public void FocusInput()
        {
            if (IsDisposed)
            {
                return;
            }

            if (Environment.CurrentManagedThreadId != _uiThreadId && IsHandleCreated)
            {
                BeginInvoke(new Action(FocusInput));
                return;
            }

            if (_isFailed)
            {
                _webView.Focus();
                return;
            }

            if (_webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.ExecuteScriptAsync("if (window.smartwordFocusInput) { window.smartwordFocusInput(); }");
            }
            else
            {
                _webView.Focus();
            }
        }

        /// <summary>
        /// 释放资源并解除事件订阅。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnbindCoreEvents();
                if (_webView != null)
                {
                    _webView.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// 初始化 WebView2 内核并导航到本地前端页面。
        /// </summary>
        private async Task InitializeCoreAsync()
        {
            string webRoot = ResolveWebRootPath();
            if (!Directory.Exists(webRoot))
            {
                MarkFailed("未找到前端资源。请先执行前端构建（npm run build）。");
                return;
            }

            try
            {
                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SmartWord",
                    "WebView2");
                Directory.CreateDirectory(userData);

                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userData).ConfigureAwait(true);
                await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

                if (_webView.CoreWebView2 == null)
                {
                    MarkFailed("WebView2 初始化失败：核心对象不可用。");
                    return;
                }

                _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;

                BindCoreEvents();

                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "smartword.app",
                    webRoot,
                    CoreWebView2HostResourceAccessKind.Allow);
                _webView.Source = new Uri("https://smartword.app/index.html");

                _errorLabel.Visible = false;
                _webView.Visible = true;
                _logger.Info("ui.webview.initialized", "WebView2 initialized successfully. RootPath={RootPath}", webRoot);
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                _logger.Error("ui.webview.runtime-missing", ex, "WebView2 runtime is missing.");
                MarkFailed("检测到未安装 WebView2 Runtime，请安装后重试。");
            }
            catch (Exception ex)
            {
                _logger.Error("ui.webview.initialize-failed", ex, "WebView2 initialization failed.");
                MarkFailed("WebView2 初始化失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 绑定 CoreWebView2 事件。
        /// </summary>
        private void BindCoreEvents()
        {
            if (_eventsBound || _webView.CoreWebView2 == null)
            {
                return;
            }

            _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            _webView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
            _eventsBound = true;
        }

        /// <summary>
        /// 解绑 CoreWebView2 事件。
        /// </summary>
        private void UnbindCoreEvents()
        {
            if (!_eventsBound || _webView == null || _webView.CoreWebView2 == null)
            {
                return;
            }

            _webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            _webView.CoreWebView2.ProcessFailed -= CoreWebView2_ProcessFailed;
            _eventsBound = false;
        }

        /// <summary>
        /// 处理前端 RPC 请求消息。
        /// </summary>
        private async void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string requestJson = e == null ? string.Empty : (e.WebMessageAsJson ?? string.Empty);
                string responseJson = await _rpcBridge.HandleAsync(requestJson).ConfigureAwait(false);
                await RunOnUiThreadAsync(() =>
                {
                    if (!IsDisposed && _webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.PostWebMessageAsJson(responseJson);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error("ui.webview.rpc-failed", ex, "Failed to handle WebView2 message.");
            }
        }

        /// <summary>
        /// 处理渲染进程失败事件。
        /// </summary>
        private void CoreWebView2_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            _logger.Error(
                "ui.webview.process-failed",
                new InvalidOperationException("WebView2 process failed: " + (e == null ? string.Empty : e.ProcessFailedKind.ToString())),
                "WebView2 process failed.");
            MarkFailed("WebView2 渲染进程异常，侧栏已禁用，请关闭并重试。");
        }

        /// <summary>
        /// 标记不可用状态并展示错误提示。
        /// </summary>
        private void MarkFailed(string message)
        {
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action<string>(MarkFailed), message);
                }

                return;
            }

            string text = string.IsNullOrWhiteSpace(message)
                ? "WebView2 初始化失败，侧栏已禁用。"
                : message.Trim();

            _isFailed = true;
            _webView.Visible = false;
            _errorLabel.Text = text;
            _errorLabel.Visible = true;
            if (_notificationService != null)
            {
                _notificationService.Error(text);
            }
        }

        /// <summary>
        /// 解析前端静态资源目录。
        /// </summary>
        private static string ResolveWebRootPath()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string outputWebPath = Path.Combine(baseDirectory, "webapp");
            if (Directory.Exists(outputWebPath))
            {
                return outputWebPath;
            }

            // 调试兜底：直接读取项目目录中的构建产物。
            return Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "WebClient", "dist"));
        }

        /// <summary>
        /// 将操作封送到 UI 线程执行。
        /// </summary>
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
    }
}
