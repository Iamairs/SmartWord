using Microsoft.Office.Tools.Ribbon;

namespace SmartWord.AddIn.UI
{
    public sealed class SmartWordRibbon : RibbonBase
    {
        private RibbonTab _homeTab;
        private RibbonGroup _smartWordGroup;
        private RibbonToggleButton _toggleChatPaneButton;

        public SmartWordRibbon()
            : base(Globals.Factory.GetRibbonFactory())
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this._homeTab = this.Factory.CreateRibbonTab();
            this._smartWordGroup = this.Factory.CreateRibbonGroup();
            this._toggleChatPaneButton = this.Factory.CreateRibbonToggleButton();
            this._homeTab.SuspendLayout();
            this._smartWordGroup.SuspendLayout();
            this.SuspendLayout();
            // 
            // _homeTab
            // 
            this._homeTab.ControlId.ControlIdType = Microsoft.Office.Tools.Ribbon.RibbonControlIdType.Office;
            this._homeTab.ControlId.OfficeId = "TabHome";
            this._homeTab.Groups.Add(this._smartWordGroup);
            this._homeTab.Label = "TabHome";
            this._homeTab.Name = "_homeTab";
            // 
            // _smartWordGroup
            // 
            this._smartWordGroup.Items.Add(this._toggleChatPaneButton);
            this._smartWordGroup.Label = "SmartWord";
            this._smartWordGroup.Name = "_smartWordGroup";
            // 
            // _toggleChatPaneButton
            // 
            this._toggleChatPaneButton.Label = "对话侧栏";
            this._toggleChatPaneButton.Name = "_toggleChatPaneButton";
            this._toggleChatPaneButton.ScreenTip = "显示或隐藏 SmartWord 对话侧栏";
            // 
            // SmartWordRibbon
            // 
            this.Name = "SmartWordRibbon";
            this.Tabs.Add(this._homeTab);
            this._homeTab.ResumeLayout(false);
            this._homeTab.PerformLayout();
            this._smartWordGroup.ResumeLayout(false);
            this._smartWordGroup.PerformLayout();
            this.ResumeLayout(false);

        }

        public void SyncPaneState(bool isVisible)
        {
            _toggleChatPaneButton.Checked = isVisible;
        }

        private void ToggleChatPaneButton_Click(object sender, RibbonControlEventArgs e)
        {
            bool current = _toggleChatPaneButton.Checked;
            bool finalVisible = Globals.ThisAddIn.SetChatPaneVisible(current);
            _toggleChatPaneButton.Checked = finalVisible;
        }
    }
}
