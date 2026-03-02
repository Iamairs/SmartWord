namespace SmartWord.Core.Abstractions
{
    public interface IVbaExecutor
    {
        void Execute(string vbaCode, string entryPoint);
    }
}
