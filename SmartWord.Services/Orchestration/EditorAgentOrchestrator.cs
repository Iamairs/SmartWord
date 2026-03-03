using System;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Models;
using SmartWord.Core.Orchestration;

namespace SmartWord.Services.Orchestration
{
    public sealed class EditorAgentOrchestrator : IEditorAgentOrchestrator
    {
        private readonly ISelectionService _selectionService;
        private readonly IModelService _modelService;
        private readonly INotificationService _notificationService;

        public EditorAgentOrchestrator(
            ISelectionService selectionService,
            IModelService modelService,
            INotificationService notificationService)
        {
            _selectionService = selectionService;
            _modelService = modelService;
            _notificationService = notificationService;
        }

        public void RunRewrite(string instruction, string modelOverride, string promptVersion)
        {
            try
            {
                string selectedText = _selectionService.GetSelectedText();
                if (string.IsNullOrWhiteSpace(selectedText))
                {
                    _notificationService.Error("Please select text first, then run Alt+K.");
                    return;
                }

                var request = new EditorRewriteRequest
                {
                    Instruction = instruction,
                    SelectedText = selectedText,
                    ModelOverride = modelOverride,
                    PromptVersion = promptVersion
                };

                string rewrittenText = _modelService.RewriteText(request);
                if (string.IsNullOrWhiteSpace(rewrittenText))
                {
                    _notificationService.Error("Model returned empty text.");
                    return;
                }

                _selectionService.ReplaceSelection(rewrittenText);
                _notificationService.Info("Rewrite completed.");
            }
            catch (Exception ex)
            {
                _notificationService.Error("Rewrite failed: " + ex.Message);
            }
        }
    }
}
