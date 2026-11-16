namespace KeyPassXCCloudSync;

// The OS adapter alone supplies an update/recovery worker. The shared Avalonia
// shell only shows progress and never resolves Windows processes or services.
internal static class UpdateHelperBootstrap
{
    internal static string[]? Arguments;
    internal static bool WorkStarted;
    internal static Func<string[],Action<UpdateOperationProgress>?,int>? Execute;

    internal static int Run(string[] args,Action<UpdateOperationProgress>? progress)
    {
        var run=Execute??throw new InvalidOperationException("Update worker is not configured.");
        return run(args,progress);
    }
}
