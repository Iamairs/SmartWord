namespace SmartWord.Core.Abstractions
{
    public interface ISelectionService
    {
        string GetSelectedText();

        void ReplaceSelection(string newText);
    }
}
