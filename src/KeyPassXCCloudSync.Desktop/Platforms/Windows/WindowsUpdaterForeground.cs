using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace KeyPassXCCloudSync;

// Only the Windows adapter grants focus and uses window handles. The shared
// Avalonia UI requests foreground via UpdateHelperBootstrap.ActivateWindow.
internal static class WindowsUpdaterForeground
{
    [DllImport("user32.dll",SetLastError=true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll",SetLastError=true)]
    private static extern bool BringWindowToTop(nint handle);
    [DllImport("user32.dll",SetLastError=true)]
    private static extern bool SetForegroundWindow(nint handle);

    internal static void AllowForeground(Process child)
    {
        // Windows allows this only when the caller has foreground rights. The
        // helper also performs a brief topmost activation as a fallback.
        _ = AllowSetForegroundWindow((uint)child.Id);
    }

    internal static void ActivateWindow(Window window)
    {
        var handle=window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if(handle==nint.Zero)return;
        _ = BringWindowToTop(handle);
        _ = SetForegroundWindow(handle);
    }
}
