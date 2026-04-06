using System;
using System.IO;
using Serilog;
using SmartWord.AddIn.DI;
using SmartWord.AddIn.Ribbon;
using SmartWord.AddIn.TaskPane;
using Office = Microsoft.Office.Core;
using RibbonTools = Microsoft.Office.Tools.Ribbon;

namespace SmartWord.AddIn
{
    public partial class ThisAddIn
    {
        private SmartWordRibbon _ribbon;
        private SmartWordTaskPaneControl _taskPaneControl;

        public Microsoft.Office.Tools.CustomTaskPane TaskPane { get; private set; }

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            try
            {
                InitializeLogging();
                Log.Information("SmartWord 插件启动。");

                ServiceLocator.Initialize(this.Application);
                InitializeTaskPane();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "SmartWord 插件启动失败。");
                throw;
            }
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            try
            {
                Log.Information("SmartWord 插件关闭。");
                ServiceLocator.Dispose();
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        public void ToggleTaskPane(bool visible)
        {
            if (this.TaskPane == null)
            {
                return;
            }

            this.TaskPane.Visible = visible;
            _ribbon?.SyncTaskPaneState(visible);
        }

        public bool IsTaskPaneVisible()
        {
            return this.TaskPane != null && this.TaskPane.Visible;
        }

        protected override Office.IRibbonExtensibility CreateRibbonExtensibilityObject()
        {
            _ribbon = _ribbon ?? new SmartWordRibbon();
            return Globals.Factory.GetRibbonFactory().CreateRibbonManager(
                new RibbonTools.IRibbonExtension[] { _ribbon });
        }

        private async void InitializeTaskPane()
        {
            try
            {
                _taskPaneControl = new SmartWordTaskPaneControl();
                this.TaskPane = this.CustomTaskPanes.Add(_taskPaneControl, "SmartWord");
                this.TaskPane.Width = 320;
                this.TaskPane.Visible = false;
                this.TaskPane.VisibleChanged += TaskPane_VisibleChanged;

                await _taskPaneControl.InitializeAsync();

                // 开发阶段默认展开侧边栏，避免 Ribbon 未显示时无法验证插件已启动。
                this.TaskPane.Visible = true;
                Log.Information("SmartWord 侧边栏初始化完成。");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "SmartWord 侧边栏初始化失败。");
            }
        }

        private void InitializeLogging()
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SmartWord",
                "Logs");

            Directory.CreateDirectory(logDirectory);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(logDirectory, "smartword-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                .CreateLogger();
        }

        private void TaskPane_VisibleChanged(object sender, EventArgs e)
        {
            _ribbon?.SyncTaskPaneState(this.TaskPane != null && this.TaskPane.Visible);
        }

        #region VSTO Generated Code

        private void InternalStartup()
        {
            this.Startup += new EventHandler(ThisAddIn_Startup);
            this.Shutdown += new EventHandler(ThisAddIn_Shutdown);
        }

        #endregion
    }
}
