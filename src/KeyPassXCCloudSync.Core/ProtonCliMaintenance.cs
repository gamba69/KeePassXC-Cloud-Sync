namespace KeyPassXCCloudSync;

// Protects entire Proton remote-store lifetimes, not individual CLI processes.
// The updater is allowed to replace an executable only between sync sessions.
internal static class ProtonCliMaintenance
{
    private static readonly object Gate=new();
    private static int _users;
    private static bool _updating;

    internal static IDisposable BeginUse()
    {
        lock(Gate)
        {
            while(_updating)Monitor.Wait(Gate);
            _users++;
            return new Lease(false);
        }
    }

    internal static IDisposable? TryBeginUpdate()
    {
        lock(Gate)
        {
            if(_updating || _users>0)return null;
            _updating=true;
            return new Lease(true);
        }
    }

    private sealed class Lease(bool update):IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock(Gate)
            {
                if(_disposed)return;
                _disposed=true;
                if(update)_updating=false;
                else _users--;
                Monitor.PulseAll(Gate);
            }
        }
    }
}
