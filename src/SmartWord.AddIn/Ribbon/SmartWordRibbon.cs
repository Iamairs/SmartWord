using Microsoft.Office.Tools.Ribbon;
using Office = Microsoft.Office.Core;
using Serilog;

namespace SmartWord.AddIn.Ribbon
{
    /// <summary>
    /// 使用 VSTO RibbonBase 构建 SmartWord 选项卡，降低 XML 回调链路的不确定性。
    /// </summary>
    public class SmartWordRibbon : RibbonBase
    {
        private RibbonTab _smartWordTab;
        private RibbonGroup _smartWordGroup;
        private RibbonToggleButton _toggleTaskPaneButton;

        public SmartWordRibbon()
            : base(Globals.Factory.GetRibbonFactory())
        {
            InitializeComponent();
        }

        public void SyncTaskPaneState(bool visible)
        {
            if (_toggleTaskPaneButton == null)
            {
                return;
            }

            _toggleTaskPaneButton.Checked = visible;
        }

        private void InitializeComponent()
        {
            _smartWordTab = this.Factory.CreateRibbonTab();
            _smartWordGroup = this.Factory.CreateRibbonGroup();
            _toggleTaskPaneButton = this.Factory.CreateRibbonToggleButton();

            _smartWordTab.Label = "SmartWord";
            _smartWordTab.Name = "smartWordTab";

            _smartWordGroup.Label = "SmartWord";
            _smartWordGroup.Name = "smartWordGroup";

            _toggleTaskPaneButton.ControlSize = Office.RibbonControlSize.RibbonControlSizeLarge;
            _toggleTaskPaneButton.OfficeImageId = "ReviewShowMarkup";
            _toggleTaskPaneButton.ShowImage = true;
            _toggleTaskPaneButton.Label = "侧边栏";
            _toggleTaskPaneButton.Name = "toggleTaskPane";
            _toggleTaskPaneButton.Click += ToggleTaskPaneButton_Click;

            _smartWordGroup.Items.Add(_toggleTaskPaneButton);
            _smartWordTab.Groups.Add(_smartWordGroup);

            this.Name = "SmartWordRibbon";
            this.RibbonType = "Microsoft.Word.Document";
            this.Tabs.Add(_smartWordTab);
        }

        private void ToggleTaskPaneButton_Click(object sender, RibbonControlEventArgs e)
        {
            Log.Information("用户切换侧边栏，状态：{IsChecked}", _toggleTaskPaneButton.Checked);
            Globals.ThisAddIn.ToggleTaskPane(_toggleTaskPaneButton.Checked);
        }
    }
}
