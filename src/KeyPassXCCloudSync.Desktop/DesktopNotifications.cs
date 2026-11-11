using Avalonia.Controls;

namespace KeyPassXCCloudSync;

// Platform-independent intent; Linux/macOS backends will implement this contract.
internal interface IDesktopNotifier : IDisposable
{
    void Send(Window owner, string title, string message);
}

internal sealed class UnsupportedDesktopNotifier : IDesktopNotifier
{
    public void Send(Window owner, string title, string message) =>
        throw new PlatformNotSupportedException("Native desktop notifications are not yet implemented on this platform.");
    public void Dispose() { }
}

