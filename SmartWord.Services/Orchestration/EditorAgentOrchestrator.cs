using System;
using System.Threading.Tasks;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Models;
using SmartWord.Core.Orchestration;

// 文件说明：
// 文本改写编排器实现，负责串联选区读取、模型改写、结果替换与用户通知。
namespace SmartWord.Services.Orchestration
{
    /// <summary>
    /// 文本改写编排器。
    /// </summary>
    public sealed class EditorAgentOrchestrator : IEditorAgentOrchestrator
    {
        private readonly ISelectionService _selectionService;
        private readonly IModelService _modelService;
        private readonly INotificationService _notificationService;

        /// <summary>
        /// 初始化改写编排器。
        /// </summary>
        /// <param name="selectionService">选区服务。</param>
        /// <param name="modelService">模型服务。</param>
        /// <param name="notificationService">通知服务。</param>
        public EditorAgentOrchestrator(
            ISelectionService selectionService,
            IModelService modelService,
            INotificationService notificationService)
        {
            _selectionService = selectionService;
            _modelService = modelService;
            _notificationService = notificationService;
        }

        /// <summary>
        /// 执行改写流程：读取选区、调用模型、回写结果并反馈状态。
        /// </summary>
        /// <param name="instruction">用户改写指令。</param>
        /// <param name="modelOverride">模型覆盖项。</param>
        /// <param name="promptVersion">Prompt 版本。</param>
        public async Task RunRewriteAsync(string instruction, string modelOverride, string promptVersion)
        {
            try
            {
                string selectedText = _selectionService.GetSelectedText();
                if (string.IsNullOrWhiteSpace(selectedText))
                {
                    // 无选区时直接提示并终止，避免覆盖未知位置内容。
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

                string rewrittenText = await _modelService.RewriteTextAsync(request);
                if (string.IsNullOrWhiteSpace(rewrittenText))
                {
                    // 防御模型空响应，避免执行空文本替换。
                    _notificationService.Error("Model returned empty text.");
                    return;
                }

                // 仅在获得有效输出后回写文档，确保改写行为可预期。
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
