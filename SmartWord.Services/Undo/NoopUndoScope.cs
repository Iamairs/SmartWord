using SmartWord.Core.Abstractions;

namespace SmartWord.Services.Undo
{
    internal sealed class NoopUndoScope : IUndoScope
    {
        public static readonly NoopUndoScope Instance = new NoopUndoScope();

        private NoopUndoScope()
        {
        }

        public void Dispose()
        {
        }
    }
}
