namespace KeyPassXCCloudSync;

internal sealed record UpdatePreferences(bool CheckAutomatically = true,
    bool DownloadAutomatically = true, string Installation = "exit");

internal sealed record UpdateSnapshot(string Status = "idle", string Version = "",
    string Detail = "", bool Unverified = false, bool Reinstall = false);

internal interface IUpdateService : IDisposable
{
    UpdatePreferences Preferences { get; }
    UpdateSnapshot Snapshot { get; }
    bool IsReady { get; }
    bool IsReinstallReady { get; }
    event Action? Changed;
    void SavePreferences(UpdatePreferences value);
    Task CheckAsync(bool manual, CancellationToken cancellationToken = default, bool reinstall = false);
    void ValidateInstallation();
    void LaunchInstaller(bool restart, string configPath, bool background, bool confirmedReinstall = false);
    void CleanupCompleted();
}

// Stateless button policy. Error status may be retried, but is never presented
// as proof that the installed release is current.
internal static class UpdateActionPolicy
{
    internal static bool OfferRepair(UpdateSnapshot state,bool reinstallReady) =>
        state.Status is "current" or "error" ||
        (state.Status=="ready" && reinstallReady && state.Reinstall);
}
