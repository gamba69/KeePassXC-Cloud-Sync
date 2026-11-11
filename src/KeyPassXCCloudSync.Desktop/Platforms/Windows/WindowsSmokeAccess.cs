using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("KeyPassXCCloudSync.UiSmokeTests")]

namespace KeyPassXCCloudSync;

// Used only by the isolated headless regression executable. It never enters
// the production single-instance lock, opens credentials or starts EngineHost.
internal static class DesktopSmokeAccess
{
    internal static void ConfigurePlatform()
    {
        PlatformServices.Configure(new WindowsDesktopPlatform());
        GuiApp.UiSmokeMode=true;
    }
}
