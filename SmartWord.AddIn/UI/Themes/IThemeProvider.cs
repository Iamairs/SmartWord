using System.Drawing;

namespace SmartWord.AddIn.UI.Themes
{
    internal interface IThemeProvider
    {
        Color BackgroundColor { get; }

        Color SecondaryBackgroundColor { get; }

        Color BorderColor { get; }

        Color TextColor { get; }

        Color AccentColor { get; }

        Font NormalFont { get; }

        Font SmallFont { get; }
    }
}
