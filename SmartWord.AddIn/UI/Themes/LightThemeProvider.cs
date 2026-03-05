using System.Drawing;

namespace SmartWord.AddIn.UI.Themes
{
    // 文件说明：
    // 提供 SmartWord 默认浅色主题，实现统一的颜色与字体资源。
    /// <summary>
    /// 浅色主题提供器。
    /// </summary>
    internal sealed class LightThemeProvider : IThemeProvider
    {
        /// <summary>
        /// 页面主背景色。
        /// </summary>
        public Color BackgroundColor
        {
            get { return Color.White; }
        }

        /// <summary>
        /// 次级背景色，用于分区容器。
        /// </summary>
        public Color SecondaryBackgroundColor
        {
            get { return Color.FromArgb(248, 248, 248); }
        }

        /// <summary>
        /// 控件边框色。
        /// </summary>
        public Color BorderColor
        {
            get { return Color.FromArgb(214, 214, 214); }
        }

        /// <summary>
        /// 主要文本色。
        /// </summary>
        public Color TextColor
        {
            get { return Color.FromArgb(32, 32, 32); }
        }

        /// <summary>
        /// 交互强调色。
        /// </summary>
        public Color AccentColor
        {
            get { return Color.FromArgb(0, 120, 215); }
        }

        /// <summary>
        /// 正文字体定义。
        /// </summary>
        public Font NormalFont
        {
            get { return new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point); }
        }

        /// <summary>
        /// 小号字体定义。
        /// </summary>
        public Font SmallFont
        {
            get { return new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point); }
        }
    }
}
