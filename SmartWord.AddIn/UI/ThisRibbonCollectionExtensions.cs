namespace SmartWord.AddIn
{
    internal sealed partial class ThisRibbonCollection
    {
        internal UI.SmartWordRibbon SmartWordRibbon
        {
            get { return this.GetRibbon<UI.SmartWordRibbon>(); }
        }
    }
}
