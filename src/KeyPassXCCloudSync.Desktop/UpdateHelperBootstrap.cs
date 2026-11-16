using Avalonia.Controls;

namespace KeyPassXCCloudSync;

// The OS adapter alone supplies an update/recovery worker. The shared Avalonia
// shell only shows progress and never resolves Windows processes or services.
internal static class UpdateHelperBootstrap
{
    internal static string[]? Arguments;
    internal static bool WorkStarted;
    internal static Func<string[],Action<UpdateOperationProgress>?,int>? Execute;
    internal static Action<Window>? ActivateWindow;

    internal static int Run(string[] args,Action<UpdateOperationProgress>? progress)
    {
        var run=Execute??throw new InvalidOperationException("Update worker is not configured.");
        return run(args,progress);
    }
}

// Only the first install/rollback phase proves that the old process exited.
// Do not repeatedly steal focus for every progress callback or at startup.
internal static class UpdateHelperForegroundPolicy
{
    internal static bool AfterOwnerExit(string phase) => phase is "installing" or "rollback";
}
