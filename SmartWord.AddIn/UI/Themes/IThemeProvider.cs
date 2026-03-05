using System.Drawing;

namespace SmartWord.AddIn.UI.Themes
{
    // 文件说明：
    // 定义侧栏 UI 主题契约，统一颜色与字体，便于后续扩展多主题实现。
    /// <summary>
    /// 主题提供器接口。
    /// </summary>
    internal interface IThemeProvider
    {
        /// <summary>
        /// 主背景色。
        /// </summary>
        Color BackgroundColor { get; }

        /// <summary>
        /// 次级背景色（常用于分区面板）。
        /// </summary>
        Color SecondaryBackgroundColor { get; }

        /// <summary>
        /// 边框色。
        /// </summary>
        Color BorderColor { get; }

        /// <summary>
        /// 正文文本色。
        /// </summary>
        Color TextColor { get; }

        /// <summary>
        /// 强调色。
        /// </summary>
        Color AccentColor { get; }

        /// <summary>
        /// 正文字体。
        /// </summary>
        Font NormalFont { get; }

        /// <summary>
        /// 小号字体。
        /// </summary>
        Font SmallFont { get; }
    }
}
