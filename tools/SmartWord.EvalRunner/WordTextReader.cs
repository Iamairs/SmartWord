using System;

namespace SmartWord.EvalRunner
{
    internal static class WordTextReader
    {
        public static string ReadText(string docxPath)
        {
            object wordApp = null;
            try
            {
                wordApp = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application"));
                ProgramAccessor.SetComProperty(wordApp, "Visible", false);
                dynamic documents = ProgramAccessor.GetComProperty(wordApp, "Documents");
                dynamic document = documents.Open(docxPath, ReadOnly: true, Visible: false);
                string text = Convert.ToString(document.Content.Text);
                document.Close(false);
                ProgramAccessor.QuitWord(wordApp);
                return text ?? string.Empty;
            }
            catch
            {
                ProgramAccessor.TryCloseWord(wordApp);
                return string.Empty;
            }
        }
    }

    internal static class ProgramAccessor
    {
        public static object GetComProperty(object target, string name)
        {
            return target.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, target, null);
        }

        public static void SetComProperty(object target, string name, object value)
        {
            target.GetType().InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, target, new[] { value });
        }

        public static void QuitWord(object wordApp)
        {
            if (wordApp == null)
            {
                return;
            }

            wordApp.GetType().InvokeMember("Quit", System.Reflection.BindingFlags.InvokeMethod, null, wordApp, new object[] { false });
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(wordApp);
        }

        public static void TryCloseWord(object wordApp)
        {
            try
            {
                QuitWord(wordApp);
            }
            catch
            {
            }
        }
    }
}
