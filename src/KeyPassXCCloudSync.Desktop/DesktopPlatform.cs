using Avalonia.Controls;

namespace KeyPassXCCloudSync;

internal interface ISystemAppearance
{
    bool IsDarkCaption(bool lightFallback);
    bool IsLightTaskbar(bool lightFallback);
}

// Desktop-specific capabilities. No Win32 types appear in shared UI code.
internal interface ITaskbarStatusIcons : IDisposable
{
    void Attach(Window window);
    void Apply(Window window,BrandStatus status,bool force=false);
}
internal interface IDesktopHost
{
    ISystemAppearance Appearance {get;}
    IInstanceCoordinator Instances {get;}
    IDesktopNotifier CreateNotifier();
    ITaskbarStatusIcons CreateTaskbarStatusIcons();
    bool BackgroundRequested {get;}
    void BindActivation(Action activateWindow);
    string RuntimeDescription {get;}
    string ProcessMemoryUsage {get;}
    string ProcessArchitectureName {get;}
    string PlatformName {get;}
    string VaultPathExample {get;}
    string DataPathExample {get;}
    string DiagnosticMonospaceName {get;}
    string DiagnosticSymbolsName {get;}
}
internal interface IInstanceSession : IDisposable
{
    bool IsPrimary {get;}
    bool ActivateExisting();
}
internal interface IInstanceCoordinator
{
    IInstanceSession Enter(string configPath,Action activate);
}
internal static class DesktopPlatform
{
    internal static IDesktopHost Current=>PlatformServices.Current as IDesktopHost
        ??throw new PlatformNotSupportedException("Desktop services are not available");
}
