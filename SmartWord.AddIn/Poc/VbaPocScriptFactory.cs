namespace SmartWord.AddIn.Poc
{
    internal static class VbaPocScriptFactory
    {
        public const string EntryPoint = "SmartWord_Run";

        public static string BuildFormatRedTextScript()
        {
            return
                "Public Sub SmartWord_Run()" + "\r\n" +
                "    ActiveDocument.Content.Font.Size = 16" + "\r\n" +
                "    MsgBox \"SmartWord VBA finished. Set document font size to 16.\", vbInformation" + "\r\n" +
                "End Sub";
        }
    }
}
