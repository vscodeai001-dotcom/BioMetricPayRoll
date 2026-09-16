using System.Threading;

namespace Payroll.Web.Services;

/// <summary>
/// Marks an EF save as a Firebase -> local-cache hydration operation.
/// Hydration must never be echoed back to Firebase, otherwise a realtime
/// synchronization loop can be created. AsyncLocal keeps the marker scoped
/// to the current asynchronous execution path.
/// </summary>
public sealed class FirebaseSyncWriteScope
{
    private static readonly AsyncLocal<int> Depth = new();

    public bool IsActive => Depth.Value > 0;

    public IDisposable Enter()
    {
        Depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Depth.Value = Math.Max(0, Depth.Value - 1);
        }
    }
}
