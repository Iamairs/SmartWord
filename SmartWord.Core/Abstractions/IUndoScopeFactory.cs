namespace SmartWord.Core.Abstractions
{
    public interface IUndoScopeFactory
    {
        IUndoScope Begin(string name);
    }
}
