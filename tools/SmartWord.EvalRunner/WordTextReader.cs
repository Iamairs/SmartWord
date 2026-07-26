using System;
using SmartWord.OfficeIntegration.ComInterop;

namespace SmartWord.EvalRunner
{
    internal static class WordTextReader
    {
        public static string ReadText(string docxPath)
        {
            object wordApp = null;
            object documents = null;
            object document = null;
            object content = null;
            try
            {
                wordApp = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application"));
                ProgramAccessor.SetComProperty(wordApp, "Visible", false);
                documents = ProgramAccessor.GetComProperty(wordApp, "Documents");
                document = ((dynamic)documents).Open(docxPath, ReadOnly: true, Visible: false);
                content = document == null ? null : ((dynamic)document).Content;
                return content == null ? string.Empty : Convert.ToString(((dynamic)content).Text) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                ProgramAccessor.TryCloseDocument(document);
                ComObjectReleaser.ReleaseOwned(content, "EvalRunner.WordTextReader.Content");
                ComObjectReleaser.ReleaseOwned(document, "EvalRunner.WordTextReader.Document");
                ComObjectReleaser.ReleaseOwned(documents, "EvalRunner.WordTextReader.Documents");
                ProgramAccessor.TryCloseWord(wordApp);
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

            try
            {
                wordApp.GetType().InvokeMember("Quit", System.Reflection.BindingFlags.InvokeMethod, null, wordApp, new object[] { false });
            }
            finally
            {
                ComObjectReleaser.FinalReleaseOwned(wordApp, "EvalRunner.WordApplication");
            }
        }

        public static void InvokeComMethod(object target, string name)
        {
            target.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, target, null);
        }

        public static void TryCloseDocument(object document)
        {
            if (document == null)
            {
                return;
            }

            try
            {
                document.GetType().InvokeMember("Close", System.Reflection.BindingFlags.InvokeMethod, null, document, new object[] { false });
            }
            catch
            {
            }
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
