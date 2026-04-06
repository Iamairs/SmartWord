using SmartWord.Core.Models;

namespace SmartWord.OfficeIntegration.WordWrappers
{
    /// <summary>
    /// 封装活动文档状态读取逻辑。
    /// </summary>
    public sealed class DocumentWrapper
    {
        private readonly dynamic _document;

        public DocumentWrapper(object document)
        {
            _document = document;
        }

        public DocumentStatus GetDocumentStatus()
        {
            if (_document == null)
            {
                return new DocumentStatus
                {
                    IsWritable = false,
                    Reason = "当前没有可用文档。"
                };
            }

            try
            {
                var isReadOnly = _document.ReadOnly;
                return new DocumentStatus
                {
                    IsWritable = !isReadOnly,
                    IsReadOnly = isReadOnly,
                    Reason = isReadOnly ? "文档处于只读状态。" : string.Empty
                };
            }
            catch
            {
                return new DocumentStatus
                {
                    IsWritable = false,
                    Reason = "读取文档状态失败。"
                };
            }
        }
    }
}
