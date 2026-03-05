using System.Drawing;

namespace SmartWord.AddIn.UI.Themes
{
    internal sealed class LightThemeProvider : IThemeProvider
    {
        public Color BackgroundColor
        {
            get { return Color.White; }
        }

        public Color SecondaryBackgroundColor
        {
            get { return Color.FromArgb(248, 248, 248); }
        }

        public Color BorderColor
        {
            get { return Color.FromArgb(214, 214, 214); }
        }

        public Color TextColor
        {
            get { return Color.FromArgb(32, 32, 32); }
        }

        public Color AccentColor
        {
            get { return Color.FromArgb(0, 120, 215); }
        }

        public Font NormalFont
        {
            get { return new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point); }
        }

        public Font SmallFont
        {
            get { return new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point); }
        }
    }
}
