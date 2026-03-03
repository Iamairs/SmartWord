namespace SmartWord.Core.Models
{
    public sealed class VbaGenerationRequest
    {
        public VbaGenerationRequest()
        {
            EntryPoint = "SmartWord_Run";
        }

        public string Instruction { get; set; }

        public string EntryPoint { get; set; }

        public string ModelOverride { get; set; }

        public string PromptVersion { get; set; }
    }
}
