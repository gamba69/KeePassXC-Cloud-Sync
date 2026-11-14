namespace KeyPassXCCloudSync;

internal sealed record UpdatePreferences(bool CheckAutomatically = true,
    bool DownloadAutomatically = true, string Installation = "exit");

internal sealed record UpdateSnapshot(string Status = "idle", string Version = "",
    string Detail = "", bool Unverified = false);

internal interface IUpdateService : IDisposable
{
    UpdatePreferences Preferences { get; }
    UpdateSnapshot Snapshot { get; }
    bool IsReady { get; }
    event Action? Changed;
    void SavePreferences(UpdatePreferences value);
    Task CheckAsync(bool manual, CancellationToken cancellationToken = default);
    void ValidateInstallation();
    void LaunchInstaller(bool restart, string configPath, bool background);
    void CleanupCompleted();
}
