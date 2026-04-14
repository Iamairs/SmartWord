using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Serilog;
using SmartWord.AddIn.DI;

namespace SmartWord.AddIn.TaskPane
{
    /// <summary>
    /// 承载 WebView2 并负责初始化前端宿主环境。
    /// </summary>
    public class SmartWordTaskPaneControl : UserControl
    {
        private const string VirtualHostName = "app.smartword.local";

        private readonly WebView2 _webView;
        private readonly SmartWordBridge _bridge;

        public SmartWordTaskPaneControl()
        {
            _bridge = new SmartWordBridge(this);
            ServiceLocator.AttachTaskPaneBridge(_bridge);
            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            this.Controls.Add(_webView);
            this.Dock = DockStyle.Fill;
        }

        public async Task InitializeAsync()
        {
            if (_webView.CoreWebView2 != null)
            {
                return;
            }

            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SmartWord",
                "WebView2");

            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            _webView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
            _webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            _webView.CoreWebView2.DOMContentLoaded += CoreWebView2_DOMContentLoaded;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

            _bridge.AttachToWebView(_webView.CoreWebView2);
            _webView.CoreWebView2.AddHostObjectToScript("bridge", _bridge);

            LoadFrontend();
        }

        private void LoadFrontend()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var webClientDirectory = Path.Combine(baseDirectory, "Resources", "WebClient");
            var localIndexPath = Path.Combine(webClientDirectory, "index.html");

            if (File.Exists(localIndexPath))
            {
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    VirtualHostName,
                    webClientDirectory,
                    CoreWebView2HostResourceAccessKind.Allow);

                var appUri = "https://" + VirtualHostName + "/index.html";
                _webView.CoreWebView2.Navigate(appUri);
                Log.Information("WebView2 通过虚拟主机加载本地前端资源：{AppUri}", appUri);
                return;
            }

            _webView.CoreWebView2.Navigate("http://localhost:5173");
            Log.Information("WebView2 回退到本地开发服务器。");
        }

        private async void CoreWebView2_DOMContentLoaded(object sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            try
            {
                var appState = await _webView.CoreWebView2.ExecuteScriptAsync(@"
                    (function () {
                        var app = document.getElementById('app');
                        return JSON.stringify({
                            url: location.href,
                            title: document.title,
                            bodyLength: document.body ? document.body.innerText.length : 0,
                            appExists: !!app,
                            appHtmlLength: app ? app.innerHTML.length : 0
                        });
                    })();");

                Log.Information("WebView2 DOMContentLoaded：{AppState}", appState);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "读取 WebView2 页面状态失败。");
            }
        }

        private void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                Log.Information("WebView2 页面导航成功。");
                return;
            }

            Log.Error("WebView2 页面导航失败：{WebErrorStatus}", e.WebErrorStatus);
        }

        private void CoreWebView2_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            Log.Error("WebView2 进程异常终止：{Kind}", e.ProcessFailedKind);
        }
    }
}
