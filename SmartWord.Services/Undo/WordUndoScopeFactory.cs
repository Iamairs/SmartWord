using System;
using SmartWord.Core.Abstractions;

namespace SmartWord.Services.Undo
{
    public sealed class WordUndoScopeFactory : IUndoScopeFactory
    {
        private readonly dynamic _wordApplication;
        private readonly INotificationService _notificationService;

        public WordUndoScopeFactory(dynamic wordApplication, INotificationService notificationService)
        {
            _wordApplication = wordApplication;
            _notificationService = notificationService;
        }

        public IUndoScope Begin(string name)
        {
            if (_wordApplication == null)
            {
                return NoopUndoScope.Instance;
            }

            try
            {
                dynamic undoRecord = _wordApplication.UndoRecord;
                if (undoRecord == null)
                {
                    _notificationService?.Info("UndoRecord is not available. Running without grouped undo.");
                    return NoopUndoScope.Instance;
                }

                undoRecord.StartCustomRecord(name);
                return new WordUndoScope(undoRecord);
            }
            catch (Exception)
            {
                _notificationService?.Info("Failed to start UndoRecord. Running without grouped undo.");
                return NoopUndoScope.Instance;
            }
        }
    }
}
