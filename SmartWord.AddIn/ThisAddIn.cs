using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SmartWord.AddIn.Infrastructure;
using SmartWord.AddIn.Poc;
using SmartWord.Core.Abstractions;
using SmartWord.Services.Undo;
using SmartWord.Services.Vba;

namespace SmartWord.AddIn
{
    public partial class ThisAddIn
    {
        private INotificationService _notificationService;
        private IUndoScopeFactory _undoScopeFactory;
        private IVbaExecutor _vbaExecutor;
        private GlobalHotKeyManager _hotKeyManager;
        private bool _isRunningPoc;

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _notificationService = new MessageBoxNotificationService();
            _undoScopeFactory = new WordUndoScopeFactory(Application, _notificationService);
            _vbaExecutor = new VbaExecutor(Application, _undoScopeFactory);

            try
            {
                _hotKeyManager = new GlobalHotKeyManager(HandleAltKHotKey);
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
        }

        private void HandleAltKHotKey()
        {
            if (!IsWordForeground())
            {
                return;
            }

            if (_isRunningPoc)
            {
                return;
            }

            _isRunningPoc = true;
            try
            {
                if (Application == null || Application.ActiveDocument == null)
                {
                    _notificationService.Error("No active document found.");
                    return;
                }

                DialogResult result = MessageBox.Show(
                    "Run SmartWord VBA PoC now? This will set whole document font size to 16.",
                    "SmartWord PoC",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result != DialogResult.Yes)
                {
                    return;
                }

                string vbaScript = VbaPocScriptFactory.BuildFormatRedTextScript();
                _vbaExecutor.Execute(vbaScript, VbaPocScriptFactory.EntryPoint);
                _notificationService.Info("PoC completed. Press Ctrl+Z to verify undo.");
            }
            catch (Exception ex)
            {
                _notificationService.Error("PoC execution failed: " + ex.Message);
            }
            finally
            {
                _isRunningPoc = false;
            }
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
