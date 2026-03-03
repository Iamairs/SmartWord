using System;
using System.Threading.Tasks;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Models;
using SmartWord.Core.Orchestration;
using SmartWord.Services.Vba;

namespace SmartWord.Services.Orchestration
{
    public sealed class VbaAgentOrchestrator : IVbaAgentOrchestrator
    {
        private readonly IModelService _modelService;
        private readonly VbaCodeSanitizer _sanitizer;
        private readonly IVbaExecutor _vbaExecutor;
        private readonly INotificationService _notificationService;

        public VbaAgentOrchestrator(
            IModelService modelService,
            VbaCodeSanitizer sanitizer,
            IVbaExecutor vbaExecutor,
            INotificationService notificationService)
        {
            _modelService = modelService;
            _sanitizer = sanitizer;
            _vbaExecutor = vbaExecutor;
            _notificationService = notificationService;
        }

        public async Task RunFormattingAsync(string instruction, string modelOverride, string promptVersion)
        {
            try
            {
                var request = new VbaGenerationRequest
                {
                    Instruction = instruction,
                    ModelOverride = modelOverride,
                    PromptVersion = promptVersion
                };

                string rawCode = await _modelService.GenerateVbaCodeAsync(request);
                string sanitizedCode = _sanitizer.SanitizeAndValidate(rawCode, request.EntryPoint);
                _vbaExecutor.Execute(sanitizedCode, request.EntryPoint);
                _notificationService.Info("Formatting completed. Press Ctrl+Z to verify undo.");
            }
            catch (Exception ex)
            {
                _notificationService.Error("Formatting failed: " + ex.Message);
            }
        }
    }
}
