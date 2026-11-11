using Avalonia.Controls;

namespace KeyPassXCCloudSync;

// Native WinRT toast is deliberately independent of the Avalonia tray icon.
// This creates neither an additional Shell_NotifyIcon nor a hidden tray icon.
internal sealed class WindowsToastNotifier : IDesktopNotifier
{
    public void Send(Window owner,string title,string message) => WindowsNativeToast.Send(title,message);
    public void Dispose() { }
}
