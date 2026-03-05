using Microsoft.Office.Tools.Ribbon;

namespace SmartWord.AddIn.UI
{
    // 文件说明：
    // 定义 Word Ribbon 上的 SmartWord 入口按钮，并与任务侧栏可见状态保持同步。
    /// <summary>
    /// SmartWord Ribbon 组件。
    /// </summary>
    public sealed class SmartWordRibbon : RibbonBase
    {
        private RibbonTab _homeTab;
        private RibbonGroup _smartWordGroup;
        private RibbonToggleButton _toggleChatPaneButton;

        /// <summary>
        /// 初始化 Ribbon 实例并创建控件树。
        /// </summary>
        public SmartWordRibbon()
            : base(Globals.Factory.GetRibbonFactory())
        {
            InitializeComponent();
        }

        /// <summary>
        /// 构建 Ribbon 控件结构与显示属性。
        /// </summary>
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

        /// <summary>
        /// 根据侧栏可见状态同步按钮选中态。
        /// </summary>
        /// <param name="isVisible">侧栏是否可见。</param>
        public void SyncPaneState(bool isVisible)
        {
            _toggleChatPaneButton.Checked = isVisible;
        }

        /// <summary>
        /// Ribbon 按钮点击事件：切换聊天侧栏显隐并回写最终状态。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void ToggleChatPaneButton_Click(object sender, RibbonControlEventArgs e)
        {
            bool current = _toggleChatPaneButton.Checked;
            bool finalVisible = Globals.ThisAddIn.SetChatPaneVisible(current);
            _toggleChatPaneButton.Checked = finalVisible;
        }
    }
}
