using SmartWord.Core.Abstractions;

namespace SmartWord.Services.Selection
{
    public sealed class WordSelectionService : ISelectionService
    {
        private readonly dynamic _wordApplication;

        public WordSelectionService(dynamic wordApplication)
        {
            _wordApplication = wordApplication;
        }

        public string GetSelectedText()
        {
            if (_wordApplication == null)
            {
                return string.Empty;
            }

            dynamic selection = _wordApplication.Selection;
            if (selection == null)
            {
                return string.Empty;
            }

            object text = selection.Text;
            return text as string ?? string.Empty;
        }

        public void ReplaceSelection(string newText)
        {
            if (_wordApplication == null)
            {
                return;
            }

            dynamic selection = _wordApplication.Selection;
            if (selection == null)
            {
                return;
            }

            selection.Text = newText ?? string.Empty;
        }
    }
}
