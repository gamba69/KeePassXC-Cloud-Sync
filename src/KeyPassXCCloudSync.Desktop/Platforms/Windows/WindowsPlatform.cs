using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace KeyPassXCCloudSync;

// Windows adapter for platform services. Core and shared UI remain OS-neutral.
internal sealed class WindowsDesktopPlatform : IPlatformServices, IDesktopHost, IDisposable
{
    private readonly WindowsDatabaseLockService _locks=new();
    private readonly WindowsCredentialStore _credentials=new();
    private readonly WindowsAutostartService _autostart=new();
    private readonly WindowsPlatformPaths _paths=new();
    private readonly WindowsBrowser _browser=new();
    private readonly WindowsProcessLauncher _processes=new();
    public ISystemAppearance Appearance {get;}=new WindowsSystemAppearance();
    public IInstanceCoordinator Instances {get;}=new WindowsInstanceCoordinator();
    public ISecretStore Secrets=>_credentials;
    public IPlatformChangeMonitoring Changes {get;}=new WindowsChangeMonitoring();
    public IPlatformCliRunner Cli {get;}=new WindowsCliRunner();
    public IAutostartService Autostart=>_autostart;
    public IDatabaseLockService DatabaseLocks=>_locks;
    public IPlatformPaths Paths=>_paths;
    public IExternalBrowser Browser=>_browser;
    public IPlatformProcessLauncher Processes=>_processes;
    public bool BackgroundRequested=>Program.BackgroundRequested;
    public void BindActivation(Action callback)=>Program.BindActivation(callback);
    public string RuntimeDescription=>RuntimeInformation.FrameworkDescription;
    public string ProcessMemoryUsage
    {
        get
        {
            try
            {
                using var process=Process.GetCurrentProcess();
                return (process.WorkingSet64/1048576d).ToString("0",System.Globalization.CultureInfo.CurrentCulture)+" MiB";
            }
            catch(Exception){return "N/A";}
        }
    }
    public string ProcessArchitectureName=>RuntimeInformation.ProcessArchitecture.ToString();
    public string PlatformName => "Windows";
    public string VaultPathExample => @"D:\Passwords\Passwords.kdbx";
    public string DataPathExample => @"D:\KeePassSync\Data";
    public string DiagnosticMonospaceName => "Consolas";
    public string DiagnosticSymbolsName => "Segoe UI Symbol";
    public IDesktopNotifier CreateNotifier()=>new WindowsToastNotifier();
    public ITaskbarStatusIcons CreateTaskbarStatusIcons()=>new WindowsTaskbarStatusIcons();
    public void Dispose()=>_locks.Dispose();
}
internal sealed class WindowsSystemAppearance : ISystemAppearance
{
    private static int? ThemeValue(string key)
    {
        try
        {
            using var reg=Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return reg?.GetValue(key) as int?;
        }
        catch(System.Security.SecurityException){return null;}
        catch(UnauthorizedAccessException){return null;}
    }
    public bool IsDarkCaption(bool lightFallback)=>ThemeValue("AppsUseLightTheme") is { } value
        ? value==0 : !IsLightTaskbar(lightFallback);
    public bool IsLightTaskbar(bool lightFallback)=>ThemeValue("SystemUsesLightTheme") is { } value
        ? value!=0 : lightFallback;
}
internal sealed class WindowsCredentialStore : ISecretStore
{
    public (string User,string Password) Read(string target)=>WindowsCredentials.Read(target);
    public void Write(string target,string user,string secret)=>WindowsCredentials.Write(target,user,secret);
    public bool Exists(string target)=>WindowsCredentials.Exists(target);
    public void RemoveUnused(IReadOnlyCollection<string> active,bool fullLeave=false)=>
        WindowsCredentials.RemoveUnused(active,fullLeave);
}
internal sealed class WindowsAutostartService : IAutostartService
{
    private const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string CurrentValue="KeyPassXC Cloud Sync";
    public bool IsEnabled()
    {
        using var key=Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(CurrentValue)!=null;
    }
    public void SetEnabled(bool enabled,string configPath)
    {
        using var key=Registry.CurrentUser.CreateSubKey(Key,true)
            ??throw new InvalidOperationException("Нет доступа к HKCU Run");
        if(!enabled)
        {
            key.DeleteValue(CurrentValue,false);
            return;
        }
        var exe=Environment.ProcessPath??throw new InvalidOperationException("Путь EXE неизвестен");
        key.SetValue(CurrentValue,"\""+exe+"\" --background --config \""+configPath+"\"");
    }
}
internal sealed class WindowsPlatformPaths : IPlatformPaths
{
    public StringComparison PathComparison=>StringComparison.OrdinalIgnoreCase;
    public string MachineName=>Environment.MachineName;
    private static string Root(string folder)=>Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData),folder);
    public string ResolveConfig(string? explicitConfig)
    {
        if(explicitConfig is not null)return Path.GetFullPath(explicitConfig);
        var portable=Path.Combine(AppContext.BaseDirectory,"KeyPassXCCloudSync.ini");
        var local=Path.Combine(Root("KeyPassXCCloudSync"),"KeyPassXCCloudSync.ini");
        return Path.GetFullPath(File.Exists(portable)?portable:local);
    }
    public string DefaultDataDirectory(string configPath)=>Path.Combine(Root("KeyPassXCCloudSync"),"data");
    public string LocalUiFile(string configPath,string fileName)=>Path.Combine(Root("KeyPassXCCloudSync"),"ui",fileName);
    public string StartupLogPath=>Path.Combine(Root("KeyPassXCCloudSync"),"startup-errors.log");
    public string ResolveProtonExecutable(string? configured,string? configPath=null)
        => ProtonCliLocator.Resolve(configured,AppContext.BaseDirectory,configPath,
            Environment.GetEnvironmentVariable("PATH"));

}
internal sealed class WindowsBrowser : IExternalBrowser
{
    public void Open(string url)=>Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
}

// All process creation and Windows shell conventions live under Platforms/Windows.
internal sealed class WindowsProcessLauncher : IPlatformProcessLauncher
{
    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
    public void StartProtonLogin(string executable)
    {
        var psi=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=false};
        psi.ArgumentList.Add("auth");psi.ArgumentList.Add("login");
        Process.Start(psi);
    }
}
