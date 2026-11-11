namespace KeyPassXCCloudSync;

// Cross-platform contracts; implementations must remain outside the core assembly.
internal interface ISecretStore
{
    (string User, string Password) Read(string target);
    void Write(string target, string user, string secret);
    bool Exists(string target);
    void RemoveUnused(IReadOnlyCollection<string> active, bool fullLeave=false);
}
internal interface IAutostartService
{
    bool IsEnabled();
    void SetEnabled(bool enabled,string configPath);
}
internal interface IDatabaseReservation : IDisposable { }
internal interface IDatabaseLockService
{
    IDatabaseReservation Acquire(string lockName);
}
internal interface IPlatformPaths
{
    StringComparison PathComparison {get;}
    string MachineName {get;}
    string ResolveConfig(string? explicitConfig);
    string DefaultDataDirectory(string configPath);
    string LocalUiFile(string configPath,string fileName);
    string StartupLogPath {get;}
    string ResolveProtonExecutable(string? configured,string? configPath=null);
}
internal interface IExternalBrowser
{
    void Open(string url);
}
internal interface IPlatformProcessLauncher
{
    void OpenFolder(string path);
    void StartProtonLogin(string executable);
}
internal interface IPlatformChangeMonitoring
{
    IDisposable WatchFile(string fullFilePath, Action changed);
    IDisposable WatchNetwork(Action changed);
}
internal interface IPlatformCliRunner
{
    Task<string> RunProtonAsync(string executable,int timeoutSeconds,
        CancellationToken cancellationToken,params string[] arguments);
}
internal interface IPlatformServices
{
    ISecretStore Secrets {get;}
    IPlatformChangeMonitoring Changes {get;}
    IPlatformCliRunner Cli {get;}
    IAutostartService Autostart {get;}
    IDatabaseLockService DatabaseLocks {get;}
    IPlatformPaths Paths {get;}
    IExternalBrowser Browser {get;}
    IPlatformProcessLauncher Processes {get;}
}
internal static class PlatformServices
{
    private static IPlatformServices? _current;
    internal static IPlatformServices Current => _current ??
        throw new InvalidOperationException("Platform services have not been initialized");
    internal static void Configure(IPlatformServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if(Interlocked.CompareExchange(ref _current,services,null)!=null)
            throw new InvalidOperationException("Platform services already initialized");
    }
}
