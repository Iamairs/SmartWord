using System;
using System.Threading.Tasks;
using SmartWord.Core.Abstractions;
using SmartWord.Core.Models;
using SmartWord.Core.Orchestration;
using SmartWord.Services.Vba;

// 文件说明：
// VBA 编排器实现，负责构建脚本生成请求并执行“生成-净化-运行”闭环。
namespace SmartWord.Services.Orchestration
{
    /// <summary>
    /// VBA 编排器。
    /// </summary>
    public sealed class VbaAgentOrchestrator : IVbaAgentOrchestrator
    {
        private readonly ISelectionService _selectionService;
        private readonly IModelService _modelService;
        private readonly VbaCodeSanitizer _sanitizer;
        private readonly IVbaExecutor _vbaExecutor;
        private readonly INotificationService _notificationService;

        /// <summary>
        /// 初始化 VBA 编排器。
        /// </summary>
        /// <param name="selectionService">选区服务。</param>
        /// <param name="modelService">模型服务。</param>
        /// <param name="sanitizer">VBA 代码净化器。</param>
        /// <param name="vbaExecutor">VBA 执行器。</param>
        /// <param name="notificationService">通知服务。</param>
        public VbaAgentOrchestrator(
            ISelectionService selectionService,
            IModelService modelService,
            VbaCodeSanitizer sanitizer,
            IVbaExecutor vbaExecutor,
            INotificationService notificationService)
        {
            _selectionService = selectionService;
            _modelService = modelService;
            _sanitizer = sanitizer;
            _vbaExecutor = vbaExecutor;
            _notificationService = notificationService;
        }

        /// <summary>
        /// 执行格式化流程：生成 VBA、净化校验并在 Word 中执行。
        /// </summary>
        /// <param name="instruction">用户格式化指令。</param>
        /// <param name="modelOverride">模型覆盖项。</param>
        /// <param name="promptVersion">Prompt 版本。</param>
        public async Task RunFormattingAsync(string instruction, string modelOverride, string promptVersion)
        {
            try
            {
                var request = new VbaGenerationRequest
                {
                    Instruction = instruction,
                    // 选区内容作为上下文输入，帮助模型定位排版目标。
                    SelectedText = _selectionService == null ? string.Empty : _selectionService.GetSelectedText(),
                    ModelOverride = modelOverride,
                    PromptVersion = promptVersion
                };

                string rawCode = await _modelService.GenerateVbaCodeAsync(request);
                // 执行前统一净化并校验入口，避免将无效脚本注入 Word。
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
