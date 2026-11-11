using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace KeyPassXCCloudSync;

public sealed class GuiApp : Application
{
    // Headless CI tests create real visual trees, but MUST NOT start the sync engine.
    internal static bool UiSmokeMode {get;set;}
    internal static string ConfigPath="";
    internal ShellWindow? Shell;
    internal EngineHost? Engine;
    internal bool HasRunningEngine=>Engine is not null;
    private TrayIcon? _tray;
    private NativeMenuItem? _trayStatus;
    private NativeMenuItem? _trayOpen,_traySync,_trayExit;
    internal IDesktopNotifier Notifications { get; private set; } = new UnsupportedDesktopNotifier();
    private readonly Dictionary<string, WindowIcon> _icons=new();
    private AppState? _lastTrayState;
    private DispatcherTimer? _trayThemeTimer;
    private bool _lastLightTaskbar;
    private bool _allowExit;
    private bool _exitConfirmationOpen;
    private bool _configured;
    private readonly Dictionary<string,DateTimeOffset> _authNotifyAfter=new(StringComparer.OrdinalIgnoreCase);
    internal void SnoozeAuth(string cloud)=>_authNotifyAfter[cloud]=DateTimeOffset.UtcNow.AddHours(24);
    private void MaybeNotifyAuth(AppState state)
    {
        if(Shell is null)return;
        var now=DateTimeOffset.UtcNow;
        var configured=Engine?.Current?.Clouds.Select(c=>c.Name) ?? Array.Empty<string>();
        var missing=ConfiguredCloudStatuses.Select(configured,state)
            .Where(x=>x.Status.State is "NEEDS_AUTH" or "AUTH_ERROR")
            .Select(x=>x.Name).Where(n=>!_authNotifyAfter.TryGetValue(n,out var later)||now>=later).ToArray();
        if(missing.Length==0)return;
        // One grouped notification, never one popup for every cloud.
        foreach(var name in missing)_authNotifyAfter[name]=now.AddHours(1);
        try{Notifications.Send(Shell,UiLang.T("KeyPassXC Cloud Sync — требуется авторизация"),
            string.Join(", ",missing)+UiLang.T(". Откройте вкладку «Облака» для настройки доступа."));}
        catch(Exception){/* toast failure must not interfere with sync */}
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if(UiSmokeMode)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _configured=File.Exists(ConfigPath);
            Shell=new ShellWindow(this,ConfigPath);
            desktop.MainWindow=Shell;
            bool trayReady=false;
            try{SetupTray();trayReady=true;}
            catch(Exception ex)
            {
                // Keep the main window visible even if Windows Shell refused
                // the tray registration. User must never be left with no UI.
                Dispatcher.UIThread.Post(()=>Shell?.ShowProblem("Значок трея недоступен",
                    UiErrorDisplay.Present(ex)));
            }
            if(_configured && DesktopPlatform.Current.BackgroundRequested && trayReady)
            {
                // Only explicitly requested background launches are hidden.
                // Never hide the window when tray setup did not succeed.
                Shell.ShowInTaskbar=false;
                Shell.WindowState=WindowState.Minimized;
                bool startupHidden=false;
                Shell.Opened+=(_,_)=>{if(!startupHidden){startupHidden=true;Shell.Hide();}};
            }
            else if(!_configured)Shell.Opened+=(_,_)=>Shell.NavigateTo(3);
            // [X] and [-] must run the same hide/geometry persistence path.
            // Never capture minimized Bounds: Shell keeps the last normal frame.
            Shell.Closing+=(_,e)=>
            {
                if(_allowExit)Shell.FlushWindowPlacement();
                else{e.Cancel=true;HideMainWindowToTray();}
            };
            // Avalonia 12 WindowState is a DirectProperty; PropertyChanged still reports it.
            Shell.PropertyChanged+=(_,e)=>
            {
                if(e.Property==Window.WindowStateProperty &&
                   Shell.WindowState==WindowState.Minimized && trayReady)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if(!_allowExit && Shell?.WindowState==WindowState.Minimized)
                            HideMainWindowToTray();
                    });
                }
            };
            Engine=new EngineHost(ConfigPath);
            Engine.ErrorReported+=(title,detail)=>Dispatcher.UIThread.Post(()=>Shell?.ShowProblem(title,detail));
            Engine.SharedConfigurationReloaded+=()=>Dispatcher.UIThread.Post(()=>
            {
                try
                {
                    Engine?.ApplyConfig(AppOptions.Load(ConfigPath));
                    Shell?.ReloadSharedConfiguration();
                }
                catch(Exception ex){Shell?.ShowProblem("Обновление топологии",ex.Message);}
            });
            Engine.ProgressUpdated+=()=>Dispatcher.UIThread.Post(()=>Shell?.RefreshLiveStatus());
            Engine.Updated+=state=> Dispatcher.UIThread.Post(()=>
            {
                Shell?.UpdateStatus(state);
                UpdateTray(state);
                MaybeNotifyAuth(state);
            });
            Engine.Start();
            Shell?.NavigateTo(0); // refresh the switch once Current is initialized
            DesktopPlatform.Current.BindActivation(()=>OpenWindow());
            if(_configured)
            {
                try{UpdateTray(SyncStorage.LoadState(AppOptions.Load(ConfigPath).StateFile));}
                catch(Exception ex){UpdateTray(null);Shell?.ShowProblem("Ошибка конфигурации",ex.Message);}
            }
            else UpdateTray(null);
        }
        base.OnFrameworkInitializationCompleted();
    }
    private WindowIcon Icon(string type)
    {
        if(_icons.TryGetValue(type,out var found))return found;
        using var stream=AssetLoader.Open(new Uri($"avares://KeyPassXCCloudSync/Assets/CloudSync_{type}.ico"));
        return _icons[type]=new WindowIcon(stream);
    }
    private void SetupTray()
    {
        // A single Avalonia TrayIcon owns the notification-area icon and
        // its theme-aware managed popup menu. Toast delivery does NOT
        // interact with Shell_NotifyIcon or this tray identity.
        Notifications=DesktopPlatform.Current.CreateNotifier();
        var menu=new NativeMenu();
        _trayStatus=new NativeMenuItem(UiLang.T("Ожидание проверки")) { IsEnabled=false };
        menu.Items.Add(_trayStatus);
        menu.Items.Add(new NativeMenuItemSeparator());
        _trayOpen=new NativeMenuItem(UiLang.T("Открыть KeyPassXC Cloud Sync"));_trayOpen.Click+=(_,_)=>OpenWindow();
        _traySync=new NativeMenuItem(UiLang.T("Синхронизировать сейчас"));_traySync.Click+=(_,_)=>{if(Engine?.SyncEnabled==true)Engine.Wake();};
        _trayExit=new NativeMenuItem(UiLang.T("Выход"));_trayExit.Click+=(_,_)=>_=RequestExitAsync();
        menu.Items.Add(_trayOpen);menu.Items.Add(_traySync);
        menu.Items.Add(new NativeMenuItemSeparator());menu.Items.Add(_trayExit);
        _lastLightTaskbar=IsLightTaskbar();
        _tray=new TrayIcon{ Icon=NormalTrayIcon(),ToolTipText="KeyPassXC Cloud Sync "+BuildInfo.Version+" · "+UiLang.T("настройка"),Menu=menu};
        _tray.Clicked+=(_,_)=>OpenWindow();
        TrayIcon.SetIcons(this,new TrayIcons{_tray});
        // Reapply after the tray handler is attached: the Shell can keep the
        // previously registered icon until the first explicit property update.
        Dispatcher.UIThread.Post(()=>UpdateTray(_lastTrayState),DispatcherPriority.Background);
        // Windows allows changing system taskbar light/dark independently from app theme.
        _trayThemeTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};
        _trayThemeTimer.Tick+=(_,_)=>
        {
            var light=IsLightTaskbar();
            if(light!=_lastLightTaskbar){_lastLightTaskbar=light;UpdateTray(_lastTrayState);}
        };
        _trayThemeTimer.Start();
    }
    internal void RefreshTrayLanguage()
    {
        if(_trayOpen is not null)_trayOpen.Header=UiLang.T("Открыть KeyPassXC Cloud Sync");
        if(_traySync is not null)_traySync.Header=UiLang.T("Синхронизировать сейчас");
        if(_trayExit is not null)_trayExit.Header=UiLang.T("Выход");
        UpdateTray(_lastTrayState);
    }
    private void HideMainWindowToTray()
    {
        if(_allowExit || Shell is not { } shell || !shell.IsVisible)return;
        shell.SuspendPlacementForHide();
        shell.ShowInTaskbar=false;
        shell.Hide();
    }

    internal void OpenWindow(int tab=-1)
    {
        // Keep the exit confirmation as the only visible KeePassSync window.
        if(_exitConfirmationOpen || Shell is null)return;
        var alreadyOpen=Shell.IsVisible && Shell.WindowState!=WindowState.Minimized;
        if(!alreadyOpen)Shell.PrepareForOpening();
        Shell.ShowInTaskbar=true;
        // A maximized state is applied only after Windows has shown the window.
        Shell.Show();
        // Window re-creation/hide-to-tray can reset the taskbar icon and overlay.
        Shell.RefreshTaskbarBrandStatus();
        if(!alreadyOpen)Shell.RestoreOpeningWindowState();
        Shell.Activate();
        if(tab>=0)Shell.NavigateTo(tab);
    }
    internal void SetTheme(string name)
    {
        RequestedThemeVariant=name switch{"Тёмная"=>ThemeVariant.Dark,"Светлая"=>ThemeVariant.Light,_=>ThemeVariant.Default};
        Shell?.RefreshTheme();
    }
    // Windows renders native caption text according to its app-window theme, not
    // Avalonia's RequestedThemeVariant and not the taskbar theme. This setting is
    // intentionally independent of KeePassSync's light/dark content theme.
    internal bool IsDarkWindowsCaption()=>DesktopPlatform.Current.Appearance.IsDarkCaption(
        RequestedThemeVariant==ThemeVariant.Light);

    private bool IsLightTaskbar()=>DesktopPlatform.Current.Appearance.IsLightTaskbar(
        RequestedThemeVariant==ThemeVariant.Light);
    private WindowIcon NormalTrayIcon()=>Icon(IsLightTaskbar()?"black":"white");


    internal void UpdateTray(AppState? state)
    {
        _lastTrayState=state;
        if(_traySync is not null)_traySync.IsEnabled=Engine?.SyncEnabled==true;
        var paused=Engine?.SyncEnabled==false;
        // Count configured connections, NOT historical entries in state.json.
        // Old/renamed/deleted clouds may still have cached success/error states.
        var configured=Engine?.Current?.Clouds.Select(c=>c.Name) ?? Array.Empty<string>();
        var active=ConfiguredCloudStatuses.Select(configured,state);
        var total=active.Count;
        var healthy=active.Count(x=>CloudHealthPresentation.IsHealthy(x.Status.State));
        var failed=active.Count(x=>CloudHealthPresentation.RequiresAttention(x.Status.State));
        var conflict=state?.Conflict==true || state?.Result=="CONFLICT";
        var severity=CloudHealthPresentation.Severity(state,active,paused);
        var fatal=severity==BrandStatus.Error;
        var warning=severity==BrandStatus.Warning;
        // One decision for tray and taskbar: no inconsistent icons after errors,
        // reconnection, pausing, or theme changes.
        Shell?.SetTaskbarBrandStatus(severity);
        if(_tray is null)return;
        var next=severity switch
        {
            BrandStatus.Error=>Icon("red"),
            BrandStatus.Warning=>Icon("yellow"),
            _=>NormalTrayIcon()
        };
        if(!ReferenceEquals(_tray.Icon,next))_tray.Icon=next;
        var label=conflict?"Конфликт версий":fatal?"Ошибка / облака недоступны":
            failed>0?"Часть облаков требует внимания":
            paused?"KDBX на паузе · топология активна":
            state?.Result=="SYNCING"?"Передача изменений":
            warning?"Часть облаков требует внимания":
            state?.Result=="SYNCED"?"Синхронизировано":
            File.Exists(ConfigPath)?"Ожидание проверки":"Требуется настройка";
        if(_trayStatus is not null)_trayStatus.Header="KeyPassXC Cloud Sync "+BuildInfo.Version+" · "+UiLang.T(label)+" · "+healthy+"/"+total+" "+UiLang.T("облаков");
        var database=Shell?.DatabaseName ?? "KDBX";
        var result=$"KeyPassXC Cloud Sync {BuildInfo.Version} · {UiLang.T(label)}\n{database} · {UiLang.T("облака")} {healthy}/{total}";
        var problem=active.FirstOrDefault(c=>CloudHealthPresentation.RequiresAttention(c.Status.State));
        if(problem?.Name is {Length:>0} name)result+="\n"+UiLang.T("Проблема:")+" "+name;
        else if(state?.LastSuccessUtc is { } checkedAt)
            result+="\n"+UiLang.T("Проверено:")+" "+checkedAt.ToLocalTime().ToString("HH:mm:ss");
        _tray.ToolTipText=result.Length>120?result[..120]:result;
    }
    private async Task RequestExitAsync()
    {
        if(_exitConfirmationOpen || _allowExit || Shell is null)return;
        var shell=Shell;
        // The tray exit must NOT open the main window behind its confirmation.
        // Preserve the previous UI state so Cancel is non-destructive.
        var wasVisible=shell.IsVisible && shell.WindowState!=WindowState.Minimized;
        var previousWindowState=shell.WindowState;
        shell.SuspendPlacementForHide();
        _exitConfirmationOpen=true;
        try
        {
            shell.Hide();
            shell.ShowInTaskbar=false;
            if(await MessageDialog.AskStandalone("Выйти из KeyPassXC Cloud Sync?",
                "Синхронизация остановится, приложение закроется. База KDBX, настройки и реквизиты останутся на месте. Для фоновой работы просто сверните окно в трей.",danger:true))
                Exit();
        }
        finally
        {
            _exitConfirmationOpen=false;
            // If the dialog was cancelled, restore only a previously visible main window.
            // A background/tray-only application must stay in the background.
            if(!_allowExit && wasVisible)
            {
                shell.ShowInTaskbar=true;
                if(shell.RememberWindowGeometry)
                {
                    shell.PrepareForOpening();
                    shell.Show();
                    shell.RestoreOpeningWindowState();
                }
                else
                {
                    shell.WindowState=previousWindowState;
                    shell.Show();
                    shell.ResumePlacementAfterHide();
                }
                shell.Activate();
            }
        }
    }
    internal void Exit()
    {
        _allowExit=true;
        Shell?.FlushWindowPlacement();
        _trayThemeTimer?.Stop();
        Engine?.Dispose();
        Notifications.Dispose();
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)desktop.Shutdown();
    }
}
