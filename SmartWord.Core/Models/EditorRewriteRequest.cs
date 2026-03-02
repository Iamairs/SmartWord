namespace SmartWord.Core.Models
{
    public sealed class EditorRewriteRequest
    {
        public string Instruction { get; set; }

        public string SelectedText { get; set; }
    }
}
