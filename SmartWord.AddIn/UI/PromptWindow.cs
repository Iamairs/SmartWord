using System.Drawing;
using System.Windows.Forms;

namespace SmartWord.AddIn.UI
{
    internal sealed class PromptWindow : Form
    {
        private readonly TextBox _instructionTextBox;
        private readonly ComboBox _modelComboBox;
        private readonly TextBox _promptVersionTextBox;
        private readonly Button _okButton;
        private readonly Button _cancelButton;

        public PromptWindow(string[] availableModels, string defaultModel, string defaultPromptVersion)
        {
            Text = "SmartWord Command";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Width = 560;
            Height = 340;

            var label = new Label
            {
                AutoSize = true,
                Left = 16,
                Top = 16,
                Text = "Instruction (default rewrite, use /vba or /format for VBA):"
            };
            Controls.Add(label);

            _instructionTextBox = new TextBox
            {
                Left = 16,
                Top = 44,
                Width = 510,
                Height = 96,
                Multiline = true,
                Font = new Font("Segoe UI", 10f)
            };
            Controls.Add(_instructionTextBox);

            var modelLabel = new Label
            {
                AutoSize = true,
                Left = 16,
                Top = 150,
                Text = "Model:"
            };
            Controls.Add(modelLabel);

            _modelComboBox = new ComboBox
            {
                Left = 16,
                Top = 172,
                Width = 250,
                DropDownStyle = ComboBoxStyle.DropDown
            };
            if (availableModels != null)
            {
                for (int i = 0; i < availableModels.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(availableModels[i]))
                    {
                        _modelComboBox.Items.Add(availableModels[i]);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(defaultModel))
            {
                _modelComboBox.Text = defaultModel;
            }
            Controls.Add(_modelComboBox);

            var promptVersionLabel = new Label
            {
                AutoSize = true,
                Left = 286,
                Top = 150,
                Text = "Prompt Version:"
            };
            Controls.Add(promptVersionLabel);

            _promptVersionTextBox = new TextBox
            {
                Left = 286,
                Top = 172,
                Width = 240,
                Text = defaultPromptVersion ?? string.Empty
            };
            Controls.Add(_promptVersionTextBox);

            _okButton = new Button
            {
                Text = "Run",
                Left = 370,
                Top = 250,
                Width = 75,
                DialogResult = DialogResult.OK
            };
            Controls.Add(_okButton);

            _cancelButton = new Button
            {
                Text = "Cancel",
                Left = 451,
                Top = 250,
                Width = 75,
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        public string Instruction
        {
            get { return _instructionTextBox.Text; }
        }

        public string SelectedModel
        {
            get { return _modelComboBox.Text; }
        }

        public string PromptVersion
        {
            get { return _promptVersionTextBox.Text; }
        }
    }
}
