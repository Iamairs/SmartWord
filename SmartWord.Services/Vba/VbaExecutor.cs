using System;
using System.Runtime.InteropServices;
using SmartWord.Core.Abstractions;
using SmartWord.Services.Undo;

namespace SmartWord.Services.Vba
{
    public sealed class VbaExecutor : IVbaExecutor
    {
        private const string DefaultUndoRecordName = "SmartWord AI Format";
        private readonly dynamic _wordApplication;
        private readonly IUndoScopeFactory _undoScopeFactory;

        public VbaExecutor(dynamic wordApplication, IUndoScopeFactory undoScopeFactory)
        {
            _wordApplication = wordApplication;
            _undoScopeFactory = undoScopeFactory;
        }

        public void Execute(string vbaCode, string entryPoint)
        {
            if (_wordApplication == null)
            {
                throw new InvalidOperationException("Word Application instance cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(vbaCode))
            {
                throw new ArgumentException("VBA code cannot be empty.", nameof(vbaCode));
            }

            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException("Entry point cannot be empty.", nameof(entryPoint));
            }

            var moduleManager = new VbaModuleManager(_wordApplication);
            string tempModuleName = null;
            IUndoScope undoScope = _undoScopeFactory?.Begin(DefaultUndoRecordName);

            if (undoScope == null)
            {
                undoScope = NoopUndoScope.Instance;
            }

            using (undoScope)
            {
                try
                {
                    tempModuleName = moduleManager.CreateTemporaryModule(vbaCode);
                    _wordApplication.Run(entryPoint);
                }
                catch (COMException ex)
                {
                    throw new InvalidOperationException("VBA execution failed. Please try rephrasing your request.", ex);
                }
                finally
                {
                    moduleManager.RemoveModuleIfExists(tempModuleName);
                }
            }
        }
    }
}
