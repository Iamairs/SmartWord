using SmartWord.Core.Abstractions;
using System;

// 文件说明：
// Word 选区服务实现，封装选区读取与替换操作，并通过线程调用器确保 COM 在主线程访问。
namespace SmartWord.Services.Selection
{
    /// <summary>
    /// Word 选区服务。
    /// </summary>
    public sealed class WordSelectionService : ISelectionService
    {
        private readonly dynamic _wordApplication;
        private readonly IWordThreadInvoker _wordThreadInvoker;

        /// <summary>
        /// 初始化选区服务。
        /// </summary>
        /// <param name="wordApplication">Word 应用实例。</param>
        /// <param name="wordThreadInvoker">Word 主线程调用器。</param>
        public WordSelectionService(dynamic wordApplication, IWordThreadInvoker wordThreadInvoker)
        {
            _wordApplication = wordApplication;
            _wordThreadInvoker = wordThreadInvoker;
        }

        /// <summary>
        /// 获取当前选中文本。
        /// </summary>
        /// <returns>选区文本；读取失败时返回空字符串。</returns>
        public string GetSelectedText()
        {
            return InvokeOnWordThread(() =>
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

                // 仅在存在真实选区时返回文本；插入点（Start == End）不应作为输入。
                try
                {
                    int start = Convert.ToInt32(selection.Start);
                    int end = Convert.ToInt32(selection.End);
                    if (start >= end)
                    {
                        return string.Empty;
                    }
                }
                catch
                {
                    // 兼容少量 Selection 代理对象读取 Start/End 失败的情况，继续走 Text 读取兜底。
                }

                object text = selection.Text;
                return text as string ?? string.Empty;
            });
        }

        /// <summary>
        /// 将当前选区替换为新文本。
        /// </summary>
        /// <param name="newText">新文本。</param>
        public void ReplaceSelection(string newText)
        {
            InvokeOnWordThread(() =>
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
            });
        }

        /// <summary>
        /// 选中指定段落范围并定位到该位置。
        /// </summary>
        /// <param name="startParagraphIndex">起始段落索引（1 基）。</param>
        /// <param name="endParagraphIndex">结束段落索引（1 基）。</param>
        public void SelectParagraphRange(int startParagraphIndex, int endParagraphIndex)
        {
            InvokeOnWordThread(() =>
            {
                if (_wordApplication == null)
                {
                    return;
                }

                if (startParagraphIndex <= 0 || endParagraphIndex <= 0)
                {
                    return;
                }

                if (endParagraphIndex < startParagraphIndex)
                {
                    endParagraphIndex = startParagraphIndex;
                }

                dynamic document = _wordApplication.ActiveDocument;
                if (document == null)
                {
                    return;
                }

                dynamic paragraphs = document.Paragraphs;
                if (paragraphs == null)
                {
                    return;
                }

                int paragraphCount = 0;
                try
                {
                    paragraphCount = Convert.ToInt32(paragraphs.Count);
                }
                catch
                {
                    paragraphCount = 0;
                }

                if (paragraphCount <= 0)
                {
                    return;
                }

                int safeStart = Math.Max(1, Math.Min(startParagraphIndex, paragraphCount));
                int safeEnd = Math.Max(safeStart, Math.Min(endParagraphIndex, paragraphCount));

                dynamic startParagraph = paragraphs[safeStart];
                dynamic endParagraph = paragraphs[safeEnd];
                if (startParagraph == null || endParagraph == null)
                {
                    return;
                }

                int rangeStart = Convert.ToInt32(startParagraph.Range.Start);
                int rangeEnd = Convert.ToInt32(endParagraph.Range.End);
                if (rangeEnd < rangeStart)
                {
                    rangeEnd = rangeStart;
                }

                dynamic selection = _wordApplication.Selection;
                if (selection == null)
                {
                    return;
                }

                selection.SetRange(rangeStart, rangeEnd);
                selection.Range.Select();
            });
        }

        /// <summary>
        /// 在 Word 主线程执行无返回值逻辑。
        /// </summary>
        /// <param name="action">待执行逻辑。</param>
        private void InvokeOnWordThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (_wordThreadInvoker == null)
            {
                action();
                return;
            }

            _wordThreadInvoker.Invoke(action);
        }

        /// <summary>
        /// 在 Word 主线程执行带返回值逻辑。
        /// </summary>
        /// <typeparam name="T">返回值类型。</typeparam>
        /// <param name="func">待执行逻辑。</param>
        /// <returns>执行结果。</returns>
        private T InvokeOnWordThread<T>(Func<T> func)
        {
            if (func == null)
            {
                return default(T);
            }

            if (_wordThreadInvoker == null)
            {
                return func();
            }

            return _wordThreadInvoker.Invoke(func);
        }
    }
}
