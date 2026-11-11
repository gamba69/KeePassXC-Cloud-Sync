using Avalonia;

namespace KeyPassXCCloudSync;

// The Windows entrypoint is the only place that chooses a native desktop host,
// registers Win32 notifications and handles Windows startup failures.
internal static class WindowsHost
{
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
        using var platform=new WindowsDesktopPlatform();
        PlatformServices.Configure(platform);
        string? specifiedConfig=null;
        for(int i=0;i<args.Length;i++)
        {
            if(args[i]=="--show")Program.ShowRequested=true;
            else if(args[i]=="--background")Program.BackgroundRequested=true;
            else if(args[i]=="--config" && specifiedConfig is null && i+1<args.Length)
                specifiedConfig=args[++i];
            else throw new ArgumentException("Неизвестный или неполный аргумент командной строки: "+args[i]);
        }
        if(Program.ShowRequested)Program.BackgroundRequested=false;
        GuiApp.ConfigPath=platform.Paths.ResolveConfig(specifiedConfig);
        WindowsStartupReporter.ConfigureLanguage(GuiApp.ConfigPath);
        // Include the resolved path to keep toast activation deterministic.
        Program.ShortcutArguments="--show --config "+Program.QuoteArgument(GuiApp.ConfigPath);
        using var session=platform.Instances.Enter(GuiApp.ConfigPath,Program.SignalActivation);
        if(!session.IsPrimary)
        {
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
