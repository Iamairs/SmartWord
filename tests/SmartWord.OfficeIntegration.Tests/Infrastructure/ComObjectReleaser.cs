using System.Runtime.InteropServices;

namespace SmartWord.OfficeIntegration.Tests.Infrastructure
{
    internal static class ComObjectReleaser
    {
        public static void FinalRelease(object value)
        {
            if (value == null)
            {
                return;
            }

            try
            {
                if (Marshal.IsComObject(value))
                {
                    Marshal.FinalReleaseComObject(value);
                }
            }
            catch
            {
                // 清理阶段采用最佳努力释放，最终由测试拥有的 PID 兜底。
            }
        }
    }
}
