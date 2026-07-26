using System;
using System.Collections.Generic;

namespace SmartWord.OfficeIntegration.ComInterop
{
    /// <summary>
    /// 按获取顺序登记本地拥有的 COM 引用，并在结束时逆序释放。
    /// </summary>
    internal sealed class ComScope : IDisposable
    {
        private readonly List<TrackedComObject> _trackedObjects = new List<TrackedComObject>();
        private readonly Action<object, string> _releaseAction;
        private bool _disposed;

        public ComScope()
            : this(ComObjectReleaser.ReleaseOwned)
        {
        }

        internal ComScope(Action<object, string> releaseAction)
        {
            _releaseAction = releaseAction ?? throw new ArgumentNullException(nameof(releaseAction));
        }

        public T Track<T>(T value, string owner)
            where T : class
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ComScope));
            }

            if (value != null && !ContainsReference(value))
            {
                _trackedObjects.Add(new TrackedComObject(value, owner));
            }

            return value;
        }

        private bool ContainsReference(object value)
        {
            foreach (var trackedObject in _trackedObjects)
            {
                if (ReferenceEquals(trackedObject.Value, value))
                {
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var index = _trackedObjects.Count - 1; index >= 0; index--)
            {
                var trackedObject = _trackedObjects[index];
                _releaseAction(trackedObject.Value, trackedObject.Owner);
            }

            _trackedObjects.Clear();
        }

        private sealed class TrackedComObject
        {
            public TrackedComObject(object value, string owner)
            {
                Value = value;
                Owner = owner ?? string.Empty;
            }

            public object Value { get; }

            public string Owner { get; }
        }
    }
}
