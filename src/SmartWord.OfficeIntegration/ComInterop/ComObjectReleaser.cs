using System;
using System.Runtime.InteropServices;
using Serilog;

namespace SmartWord.OfficeIntegration.ComInterop
{
    /// <summary>
    /// 统一释放当前调用明确拥有的 COM 引用。
    /// </summary>
    public static class ComObjectReleaser
    {
        public static void ReleaseOwned(object comObject, string owner)
        {
            Release(comObject, owner, false);
        }

        public static void FinalReleaseOwned(object comObject, string owner)
        {
            Release(comObject, owner, true);
        }

        public static bool IsComObject(object value)
        {
            return value != null && Marshal.IsComObject(value);
        }

        private static void Release(object comObject, string owner, bool finalRelease)
        {
            if (comObject == null)
            {
                return;
            }

            try
            {
                if (!Marshal.IsComObject(comObject))
                {
                    return;
                }

                if (finalRelease)
                {
                    Marshal.FinalReleaseComObject(comObject);
                }
                else
                {
                    Marshal.ReleaseComObject(comObject);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "释放 COM 对象失败。Owner={Owner}, ComType={ComType}, FinalRelease={FinalRelease}",
                    owner ?? string.Empty,
                    SafeGetTypeName(comObject),
                    finalRelease);
            }
        }

        private static string SafeGetTypeName(object value)
        {
            try
            {
                return value == null ? string.Empty : value.GetType().FullName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
