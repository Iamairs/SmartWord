using System.Text.RegularExpressions;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Models;

namespace SmartWord.Services.Model
{
    public sealed class LocalModelService : IModelService
    {
        public string RewriteText(EditorRewriteRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SelectedText))
            {
                return string.Empty;
            }

            string instruction = request.Instruction ?? string.Empty;
            string selectedText = request.SelectedText;

            if (instruction.IndexOf("upper", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return selectedText.ToUpperInvariant();
            }

            if (instruction.IndexOf("lower", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return selectedText.ToLowerInvariant();
            }

            if (instruction.IndexOf("formal", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Please note: " + selectedText;
            }

            return selectedText + " [Edited]";
        }

        public string GenerateVbaCode(VbaGenerationRequest request)
        {
            int fontSize = 16;
            if (request != null && !string.IsNullOrWhiteSpace(request.Instruction))
            {
                Match match = Regex.Match(request.Instruction, "(\\d+)");
                int parsed;
                if (match.Success && int.TryParse(match.Value, out parsed) && parsed >= 6 && parsed <= 96)
                {
                    fontSize = parsed;
                }
            }

            return
                "Public Sub SmartWord_Run()" + "\r\n" +
                "    ActiveDocument.Content.Font.Size = " + fontSize + "\r\n" +
                "End Sub";
        }
    }
}
