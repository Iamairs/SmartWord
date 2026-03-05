using System.Drawing;
using System.Windows.Forms;

namespace SmartWord.AddIn.UI
{
    // 文件说明：
    // 传统指令输入弹窗，用于在未使用侧栏时快速收集用户指令、模型与 Prompt 版本参数。
    /// <summary>
    /// 指令输入窗口。
    /// </summary>
    internal sealed class PromptWindow : Form
    {
        private readonly TextBox _instructionTextBox;
        private readonly ComboBox _modelComboBox;
        private readonly TextBox _promptVersionTextBox;
        private readonly Button _okButton;
        private readonly Button _cancelButton;

        /// <summary>
        /// 初始化指令输入窗口并构建基础表单布局。
        /// </summary>
        /// <param name="availableModels">可选模型列表。</param>
        /// <param name="defaultModel">默认模型。</param>
        /// <param name="defaultPromptVersion">默认 Prompt 版本。</param>
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
                // 仅注入有效模型名称，避免空白项污染下拉体验。
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

            // 设置默认确认/取消按钮，提升键盘可用性（Enter / Esc）。
            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        /// <summary>
        /// 获取用户输入的自然语言指令文本。
        /// </summary>
        public string Instruction
        {
            get { return _instructionTextBox.Text; }
        }

        /// <summary>
        /// 获取用户选择或输入的模型名称。
        /// </summary>
        public string SelectedModel
        {
            get { return _modelComboBox.Text; }
        }

        /// <summary>
        /// 获取用户指定的 Prompt 版本。
        /// </summary>
        public string PromptVersion
        {
            get { return _promptVersionTextBox.Text; }
        }
    }
}
