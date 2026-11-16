using Avalonia;

namespace KeyPassXCCloudSync;

// The Windows entrypoint is the only place that chooses a native desktop host,
// registers Win32 notifications and handles Windows startup failures.
internal static class WindowsHost
{
    // Parse ordinary launches without starting the UI or synchronization engine.
    // The maintenance flag is process-local and never becomes a shortcut argument.
    internal static (bool Show,bool Background,bool Reinstall,string? Config) ParseNormalArguments(string[] args)
    {
        bool show=false, background=false, reinstall=false;
        string? config=null;
        for (var i=0;i<args.Length;i++)
        {
            switch (args[i])
            {
                case "--show": show=true; break;
                case "--background": background=true; break;
                case "--reinstall" when !reinstall: reinstall=true; break;
                case "--config" when config is null && i+1<args.Length &&
                    !string.IsNullOrWhiteSpace(args[i+1]) &&
                    !args[i+1].StartsWith("--",StringComparison.Ordinal):
                    config=args[++i]; break;
                default:
                    throw new ArgumentException("Неизвестный или неполный аргумент командной строки: "+args[i]);
            }
        }
        return (show || reinstall, background && !show && !reinstall, reinstall, config);
    }

    // Recovery may relaunch the application after restoring an interrupted
    // installation; a one-shot maintenance flag must not survive that restart.
    internal static string[] RecoveryStartupArguments(string[] args) =>
        args.Where(arg=>arg!="--reinstall").ToArray();

    [STAThread]
    public static int Start(string[] args)
    {
        try{return Run(args);}
        catch(Exception ex){WindowsStartupReporter.Report(ex);return 1;}
    }
    private static int Run(string[] args)
    {
        if(!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This distribution provides a Windows implementation only.");
        if(args.Length==1 && args[0]=="--selftest-update")return 0;
        if(args.Length>0 && args[0] is "--apply-update" or "--recover-update")
        {
            UpdateHelperBootstrap.Arguments=args;
            UpdateHelperBootstrap.Execute=(workerArgs,progress)=>workerArgs[0]=="--apply-update"
                ?WindowsUpdateService.ApplyUpdate(workerArgs,progress)
                :WindowsUpdateService.ApplyRecovery(workerArgs,progress);
            UpdateHelperBootstrap.ActivateWindow=WindowsUpdaterForeground.ActivateWindow;
            try
            {
                // AOT helper displays only the Avalonia operation window.
                var code=AppBuilder.Configure<GuiApp>().UsePlatformDetect()
                    .StartWithClassicDesktopLifetime(Array.Empty<string>());
                return Environment.ExitCode!=0?Environment.ExitCode:code;
            }
            catch(Exception) when(!UpdateHelperBootstrap.WorkStarted)
            {
                // Fallback is safe only before the worker started applying files.
                return args[0]=="--apply-update"
                    ?WindowsUpdateService.ApplyUpdate(args)
                    :WindowsUpdateService.ApplyRecovery(args);
            }
        }
        if(WindowsUpdateService.LaunchInterruptedRecovery(RecoveryStartupArguments(args)))return 0;
        var options=ParseNormalArguments(args);
        using var platform=new WindowsDesktopPlatform();
        PlatformServices.Configure(platform);
        Program.ShowRequested=options.Show;
        Program.BackgroundRequested=options.Background;
        Program.ReinstallRequested=options.Reinstall;
        GuiApp.ConfigPath=platform.Paths.ResolveConfig(options.Config);
        WindowsStartupReporter.ConfigureLanguage(GuiApp.ConfigPath);
        // Include the resolved path to keep toast activation deterministic.
        Program.ShortcutArguments="--show --config "+Program.QuoteArgument(GuiApp.ConfigPath);
        using var session=platform.Instances.Enter(GuiApp.ConfigPath,Program.SignalActivation);
        if(!session.IsPrimary)
        {
            if(Program.ReinstallRequested)
            {
                // The argument cannot be forwarded to an already-running instance.
                // Never pretend that the requested maintenance operation started.
                WindowsStartupReporter.ExplainExistingInstance();
                return 1;
            }
            if(!Program.BackgroundRequested && !session.ActivateExisting())
                WindowsStartupReporter.ExplainExistingInstance();
            // No second sync engine; an existing process owns the selected vault.
            return 0;
        }
        try{WindowsNativeToast.Prepare();}
        catch(Exception ex)
        {
            try
            {
                var log=platform.Paths.StartupLogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log,DateTimeOffset.Now.ToString("O")+" Toast registration: "+
                    ex.GetType().Name+": "+ex.Message+Environment.NewLine);
            }
            catch(IOException){}catch(UnauthorizedAccessException){}
        }
        try{return AppBuilder.Configure<GuiApp>().UsePlatformDetect()
            .StartWithClassicDesktopLifetime(Array.Empty<string>());}
        finally{Program.UnbindActivation();}
    }
}
