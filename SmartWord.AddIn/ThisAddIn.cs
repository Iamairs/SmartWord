using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SmartWord.AddIn.Infrastructure;
using SmartWord.AddIn.UI;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Orchestration;
using SmartWord.Services.Model;
using SmartWord.Services.Orchestration;
using SmartWord.Services.Selection;
using SmartWord.Services.Undo;
using SmartWord.Services.Vba;

namespace SmartWord.AddIn
{
    public partial class ThisAddIn
    {
        private INotificationService _notificationService;
        private IUndoScopeFactory _undoScopeFactory;
        private IVbaExecutor _vbaExecutor;
        private IEditorAgentOrchestrator _editorAgentOrchestrator;
        private IVbaAgentOrchestrator _vbaAgentOrchestrator;
        private GlobalHotKeyManager _hotKeyManager;
        private bool _isRunningCommand;
        private OpenAiApiOptions _apiOptions;
        private string[] _availableModels = new string[0];
        private string _defaultModel = string.Empty;
        private string _defaultPromptVersion = string.Empty;

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            _notificationService = new MessageBoxNotificationService();
            _undoScopeFactory = new WordUndoScopeFactory(Application, _notificationService);
            _vbaExecutor = new VbaExecutor(Application, _undoScopeFactory);
            var selectionService = new WordSelectionService(Application);
            _apiOptions = OpenAiApiOptions.LoadFromEnvironment(AppDomain.CurrentDomain.BaseDirectory);
            var modelService = CreateModelService(_apiOptions);
            var sanitizer = new VbaCodeSanitizer();
            _editorAgentOrchestrator = new EditorAgentOrchestrator(selectionService, modelService, _notificationService);
            _vbaAgentOrchestrator = new VbaAgentOrchestrator(selectionService, modelService, sanitizer, _vbaExecutor, _notificationService);
            _availableModels = _apiOptions.AvailableModels ?? new string[0];
            _defaultModel = _apiOptions.Model ?? string.Empty;
            _defaultPromptVersion = _apiOptions.DefaultPromptVersion ?? string.Empty;

            try
            {
                _hotKeyManager = new GlobalHotKeyManager(() =>
                {
                    _ = HandleAltKHotKeyAsync();
                });
                _hotKeyManager.RegisterAltK();
            }
            catch (Exception ex)
            {
                _notificationService.Error("Hotkey registration failed: " + ex.Message);
            }
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            if (_hotKeyManager != null)
            {
                _hotKeyManager.Dispose();
                _hotKeyManager = null;
            }
        }

        private async Task HandleAltKHotKeyAsync()
        {
            if (!IsWordForeground())
            {
                return;
            }

            if (_isRunningCommand)
            {
                return;
            }

            _isRunningCommand = true;
            try
            {
                if (Application == null || Application.ActiveDocument == null)
                {
                    _notificationService.Error("No active document found.");
                    return;
                }

                using (var promptWindow = new PromptWindow(_availableModels, _defaultModel, _defaultPromptVersion))
                {
                    if (promptWindow.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    {
                        return;
                    }

                    string instruction = promptWindow.Instruction == null ? string.Empty : promptWindow.Instruction.Trim();
                    var command = ParseCommand(instruction, promptWindow.SelectedModel, promptWindow.PromptVersion);
                    if (command.Instruction.Length == 0)
                    {
                        _notificationService.Info("Instruction is empty.");
                        return;
                    }

                    if (command.IsVba)
                    {
                        await _vbaAgentOrchestrator.RunFormattingAsync(command.Instruction, command.ModelOverride, command.PromptVersion);
                    }
                    else
                    {
                        await _editorAgentOrchestrator.RunRewriteAsync(command.Instruction, command.ModelOverride, command.PromptVersion);
                    }
                }
            }
            catch (Exception ex)
            {
                _notificationService.Error("Command execution failed: " + ex.Message);
            }
            finally
            {
                _isRunningCommand = false;
            }
        }

        private static CommandExecutionOptions ParseCommand(string instruction, string selectedModel, string selectedPromptVersion)
        {
            string working = instruction == null ? string.Empty : instruction.Trim();
            bool isVba = false;

            if (working.StartsWith("/vba", StringComparison.OrdinalIgnoreCase))
            {
                working = RemoveLeadingDirective(working, "/vba");
                isVba = true;
            }
            else if (working.StartsWith("/format", StringComparison.OrdinalIgnoreCase))
            {
                working = RemoveLeadingDirective(working, "/format");
                isVba = true;
            }

            string modelOverride = string.IsNullOrWhiteSpace(selectedModel) ? string.Empty : selectedModel.Trim();
            string promptVersion = string.IsNullOrWhiteSpace(selectedPromptVersion) ? string.Empty : selectedPromptVersion.Trim();

            working = ExtractInlineOption(working, "/model", ref modelOverride);
            working = ExtractInlineOption(working, "/prompt", ref promptVersion);

            return new CommandExecutionOptions
            {
                IsVba = isVba,
                Instruction = working.Trim(),
                ModelOverride = modelOverride,
                PromptVersion = promptVersion
            };
        }

        private static string RemoveLeadingDirective(string input, string directive)
        {
            if (input.Length <= directive.Length)
            {
                return string.Empty;
            }

            return input.Substring(directive.Length).Trim();
        }

        private static string ExtractInlineOption(string input, string optionName, ref string targetValue)
        {
            string pattern = @"(?:^|\s)" + Regex.Escape(optionName) + @"\s+([^\s]+)";
            Match match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return input;
            }

            targetValue = match.Groups[1].Value.Trim();

            var spans = new List<Tuple<int, int>>();
            spans.Add(Tuple.Create(match.Index, match.Length));
            return RemoveSpans(input, spans).Trim();
        }

        private static string RemoveSpans(string text, List<Tuple<int, int>> spans)
        {
            if (spans == null || spans.Count == 0)
            {
                return text;
            }

            spans.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            int cursor = 0;
            var builder = new System.Text.StringBuilder(text.Length);

            for (int i = 0; i < spans.Count; i++)
            {
                int start = spans[i].Item1;
                int length = spans[i].Item2;
                if (start > cursor)
                {
                    builder.Append(text.Substring(cursor, start - cursor));
                }

                cursor = Math.Min(text.Length, start + length);
            }

            if (cursor < text.Length)
            {
                builder.Append(text.Substring(cursor));
            }

            return builder.ToString();
        }

        private IModelService CreateModelService(OpenAiApiOptions options)
        {
            if (options.IsConfigured)
            {
                try
                {
                    return new OpenAiCompatibleModelService(options);
                }
                catch (Exception ex)
                {
                    _notificationService.Error("Failed to initialize OpenAI model service, fallback to local model: " + ex.Message);
                    return new LocalModelService();
                }
            }

            return new LocalModelService();
        }

        private sealed class CommandExecutionOptions
        {
            public bool IsVba { get; set; }

            public string Instruction { get; set; }

            public string ModelOverride { get; set; }

            public string PromptVersion { get; set; }
        }

        private bool IsWordForeground()
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                return false;
            }

            uint foregroundPid;
            GetWindowThreadProcessId(foregroundWindow, out foregroundPid);

            int currentPid = Process.GetCurrentProcess().Id;
            return foregroundPid == currentPid;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        #region VSTO generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InternalStartup()
        {
            Startup += new EventHandler(ThisAddIn_Startup);
            Shutdown += new EventHandler(ThisAddIn_Shutdown);
        }

        #endregion
    }
}
