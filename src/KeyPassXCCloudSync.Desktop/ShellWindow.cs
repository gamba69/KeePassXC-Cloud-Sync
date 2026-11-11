using Avalonia.Controls.Templates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;

namespace KeyPassXCCloudSync;

internal sealed partial class ShellWindow : Window
{
    private sealed record LanguageChoice(string Code,string Full,string Short);

    private readonly GuiApp _app;
    private readonly string _configPath;
    private SettingsDraft _draft;
    private AppState? _state;
    // Native TabItem headers styled with the source Card Print TabItem styles.
    private ContentControl _pageContent=null!;
    private TabItem[] _tabButtons=[];
    private int _page;
    private string _selectedCloud="";
    private ScrollViewer? _diagnosticsLogScroll;
    private StackPanel? _diagnosticsRows;
    private int _diagnosticsRenderedCount;
    private TextBlock? _diagnosticsStatus;
    private ProgressBar? _diagnosticsProgress;
    private Button[] _diagnosticsActions=[];
    private Button? _diagnosticsCancel;
    private ComboBox? _diagnosticsCloudSelector;
    private CancellationTokenSource? _testCancellation;
    private readonly ConcurrentQueue<string> _pendingTestOutput=new();
    private readonly StringBuilder _testOutput=new();
    private readonly List<(string Time,string Raw)> _testRawLines=new();
    private int _testOutputDispatchScheduled;
    private readonly Stopwatch _testElapsed=new();
    private TimeSpan _testLastWorkerOutput;
    private TimeSpan _testLastHeartbeat;
    private bool _testWaitingForGate;
    private string _testModeName="";
    private string _testCurrentStep="";
    private TextBlock? _liveStatus;
    private TextBlock? _statusHeadline;
    private readonly List<(TextBlock Label,string CloudId)> _pollCountdownLabels=[];
    private readonly List<(TextBlock Label,string CloudId,int TimeoutSeconds)> _cloudActivityLabels=[];
    private readonly List<(TextBlock Label,string CloudId,string IdleState)> _cloudStateLabels=[];
    private readonly DispatcherTimer _liveTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly ITaskbarStatusIcons _taskbarStatusIcons=DesktopPlatform.Current.CreateTaskbarStatusIcons();
    private BrandStatus _taskbarStatus;
    private Image? _headerBrandImage;
    private Image? _aboutBrandImage;
    private bool _rememberWindowPosition;
    private bool _placementReady;
    private bool _applyingPlacement;
    // _normalPlacement is the last trusted normal-window rectangle. Never replace it
    // with an intermediate Bounds value while Avalonia is applying restored geometry.
    private WindowPlacement? _normalPlacement;
    private WindowPlacement? _pendingPlacement;
    // Maximized is independent from the last normal rectangle. Never replace
    // the restore bounds with the work-area bounds of a maximized window.
    private bool _rememberedMaximized;
    private bool _maximizeOnOpening;
    private bool _captureAfterSettle;
    // Hide/Show can raise a synthetic Normal transition even after Maximized.
    private bool _hidingWindow;
    private bool _settlingPlacement;
    private readonly DispatcherTimer _placementSaveTimer=new(){Interval=TimeSpan.FromMilliseconds(500)};
    private readonly DispatcherTimer _placementRestoreTimer=new(){Interval=TimeSpan.FromMilliseconds(400)};
    private bool _switchingSync;
    private bool _testing,_saving,_authenticating;
    private bool _showArchivedComputers;
    private bool _showArchivedClouds;
    private string UsageUiPath=>PlatformServices.Current.Paths.LocalUiFile(_configPath,"usage-ui.txt");
    private void SaveUsageUi()
    {
        try{Directory.CreateDirectory(Path.GetDirectoryName(UsageUiPath)!);
            File.WriteAllText(UsageUiPath,(_showArchivedComputers?"true":"false")+"|"+
                (_showArchivedClouds?"true":"false"));}
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private string _lastTest="Проверки ещё не запускались.";
    private string _lastResult="Ожидание";
    private bool Dark=>_draft.Theme=="Тёмная" ||
        (_draft.Theme=="Системная" && ActualThemeVariant==ThemeVariant.Dark);
    private Color BackgroundColor=>Color.Parse(Dark?"#101821":"#F4F7FB");
    private IBrush Fore=>new SolidColorBrush(Color.Parse(Dark?"#E9F0F9":"#182B40"));
    private IBrush Secondary=>new SolidColorBrush(Color.Parse(Dark?"#A2B6C9":"#64788D"));
    private IBrush Good=>new SolidColorBrush(Color.Parse(Dark?"#51CC93":"#17875C"));
    private IBrush ThemedBrush(string resourceKey,string darkFallback,string lightFallback)
    {
        // Application resolves theme-specific resources without depending on
        // resource lookup extensions from individual Avalonia controls.
        if(Application.Current is { } app &&
            app.TryGetResource(resourceKey,ActualThemeVariant,out var resource) &&
            resource is IBrush brush)
            return brush;
        return new SolidColorBrush(Color.Parse(Dark?darkFallback:lightFallback));
    }
    private IBrush ZebraStripeBrush()=>ThemedBrush("KpZebraBrush","#1d3246","#eef3f8");

    // Keep the familiar opening width while allowing narrower manual resizing.
    // At 820 DIP two standard dashboard cards still fit side by side.
    internal const double MinimumWindowWidth=820;
    internal const double DefaultWindowWidth=910;

    internal ShellWindow(GuiApp app,string configPath)
    {
        _app=app;_configPath=configPath;
        try{_draft=SettingsDraft.Load(configPath);}
        catch(Exception ex)
        {
            _draft=new SettingsDraft{DataDirectory=PlatformServices.Current.Paths.DefaultDataDirectory(configPath)};
            _lastTest="Ошибка конфигурации: "+UiErrorDisplay.Present(ex);
        }
        try
        {
            if(File.Exists(UsageUiPath))
            {
                var parts=File.ReadAllText(UsageUiPath).Trim().Split('|');
                if(parts.Length==2 && bool.TryParse(parts[0],out var computers) &&
                   bool.TryParse(parts[1],out var clouds))
                {
                    _showArchivedComputers=computers;
                    _showArchivedClouds=clouds;
                }
            }
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
        UiLang.Use(_draft.Language);
        _rememberWindowPosition=_draft.RememberWindowPosition;
        Title="KeyPassXC Cloud Sync "+BuildInfo.Version;
        MinWidth=MinimumWindowWidth;MinHeight=640;Width=DefaultWindowWidth;Height=MinHeight;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        if(_draft.Theme is "Светлая" or "Тёмная")_app.RequestedThemeVariant=
            _draft.Theme=="Тёмная"?ThemeVariant.Dark:ThemeVariant.Light;
        // Match Card Print: stable white titlebar/EXE branding, regardless of theme.
        Icon=AppWindowIcons.White;
        _taskbarStatusIcons.Attach(this); // hook before the first native taskbar button
        // Restore the last known state before the engine starts its asynchronous scan.
        // This prevents the first paint from incorrectly reporting missing setup.
        if(File.Exists(configPath))
        {
            try
            {
                var cfg=AppOptions.Load(configPath);
                if(File.Exists(cfg.StateFile))_state=SyncStorage.LoadState(cfg.StateFile);
            }
            catch(Exception ex){_lastTest="Не удалось прочитать состояние: "+UiErrorDisplay.Present(ex);}
        }
        BuildShell();
        ActualThemeVariantChanged+=(_,_)=>
        {
            if(_draft.Theme=="Системная")BuildShell();
            RefreshBrandArtworks();
        };
        LayoutUpdated+=(_,_)=>RefreshBrandArtworks();
        _placementSaveTimer.Tick+=(_,_)=>{_placementSaveTimer.Stop();CommitPendingPlacement();};
        _placementRestoreTimer.Tick+=(_,_)=>
        {
            _placementRestoreTimer.Stop();
            _settlingPlacement=false;
            // Programmatic reopens and native maximize/restore transitions can
            // dispatch WindowState/SizeChanged after the requested assignment.
            // Reconcile the actual native state only after layout has settled.
            if(!_hidingWindow && _rememberWindowPosition && IsVisible && WindowState!=WindowState.Minimized)
            {
                if(WindowState==WindowState.Maximized && !_rememberedMaximized)
                {
                    _rememberedMaximized=true;
                    PersistNormalPlacement();
                }
                else if(WindowState==WindowState.Normal && _rememberedMaximized)
                {
                    _rememberedMaximized=false;
                    PersistNormalPlacement();
                    _captureAfterSettle=true;
                }
                if(_captureAfterSettle)
                {
                    _captureAfterSettle=false;
                    CaptureNormalPlacement();
                }
            }
        };
        Opened+=(_,_)=>
        {
            // Native Screens exist only after window creation. Do not capture the
            // initial window Bounds immediately after assigning restored Width/Height:
            // layout may still have the old Bounds and would overwrite the saved size.
            _placementReady=true;
            if(!_rememberWindowPosition || WindowState==WindowState.Minimized)return;
            var saved=WindowPlacementStore.Load(_configPath);
            if(saved is not null)
            {
                if(!RestoreWindowPlacement(saved))CenterAtDefaultSize();
                RestoreOpeningWindowState();
            }
            else CaptureNormalPlacement();
        };
        // Defer taskbar registration until after the native button is created.
        Opened+=(_,_)=>Dispatcher.UIThread.Post(
            ()=>_taskbarStatusIcons.Apply(this,_taskbarStatus,force:true),
            DispatcherPriority.Background);
        PositionChanged+=(_,_)=>CaptureNormalPlacement();
        SizeChanged+=(_,_)=>CaptureNormalPlacement();
        // Avalonia 12: WindowState is a DirectProperty, not a StyledProperty.
        // PropertyChanged remains supported; never set WindowState via a Style.
        PropertyChanged+=(_,e)=>
        {
            if(e.Property==Window.WindowStateProperty)ObserveWindowState();
        };
        // Only the one-line activity text is refreshed every second. No page rebuilds.
        _liveTimer.Tick+=(_,_)=>RefreshLiveStatus();
        _liveTimer.Start();
        Closed+=(_,_)=>
        {
            _taskbarStatusIcons.Dispose();
            CancelProtectedDataScan();
            FlushWindowPlacement();
            _placementSaveTimer.Stop();
            _placementRestoreTimer.Stop();
            _liveTimer.Stop();
        };
        // WrapPanel reflows automatically when the window changes size.
        // Rebuilding pages on every resize would repeatedly reload statistics.
    }
    // The tray opening policy is the same whether the window was hidden with
    // [X] or [-]. Restoring a saved size is a UI operation, not a sync action.
    internal void PrepareForOpening()
    {
        if(_rememberWindowPosition)
        {
            var previous=_pendingPlacement??_normalPlacement??WindowPlacementStore.Load(_configPath);
            if(previous is not null && RestoreWindowPlacement(
                previous with {Maximized=(_normalPlacement is not null || _pendingPlacement is not null)?
                    _rememberedMaximized:previous.Maximized}))return;
        }
        CenterAtDefaultSize();
    }

    // This must run AFTER Show(): setting Maximized while the window is hidden
    // (or before native restore from Minimized) is not reliable on Windows.
    internal void RestoreOpeningWindowState()
    {
        var target=_rememberWindowPosition && _maximizeOnOpening?
            WindowState.Maximized:WindowState.Normal;
        _applyingPlacement=true;
        try{if(WindowState!=target)WindowState=target;}
        finally{_applyingPlacement=false;_hidingWindow=false;}
    }

    private void ObserveWindowState()
    {
        if(!_placementReady || !_rememberWindowPosition || _applyingPlacement ||
            _hidingWindow || !IsVisible)return;
        // Capture maximizing immediately, even within the settle window: the
        // user may press [-] before the 400ms timer has fired.
        if(WindowState==WindowState.Maximized)
        {
            // The last Normal bounds must be committed BEFORE maximization.
            if(_pendingPlacement is not null)CommitPendingPlacement();
            if(!_rememberedMaximized)
            {
                _rememberedMaximized=true;
                PersistNormalPlacement();
            }
        }
        else if(WindowState==WindowState.Normal && _rememberedMaximized)
        {
            _rememberedMaximized=false;
            PersistNormalPlacement();
            // Don't capture a maximized Bounds from the native transition.
            _captureAfterSettle=true;
            _settlingPlacement=true;
            _placementRestoreTimer.Stop();
            _placementRestoreTimer.Start();
        }
        // Minimized must NEVER clear the remembered Maximized state.
    }

    internal bool RememberWindowGeometry=>_rememberWindowPosition;

    internal void UpdateWindowPlacementPreference(bool remember)
    {
        _rememberWindowPosition=remember;
        if(!remember)
        {
            _hidingWindow=false;
            _rememberedMaximized=false;
            _maximizeOnOpening=false;
            _placementSaveTimer.Stop();
            _pendingPlacement=null;
            _normalPlacement=null;
        }
        else
        {
            _rememberedMaximized=WindowState==WindowState.Maximized;
            _maximizeOnOpening=_rememberedMaximized;
            if(_rememberedMaximized && _normalPlacement is null)
            {
                // The toggle can be enabled while already maximized. Native
                // Bounds then describe the whole monitor, never restore bounds.
                // Use the old normal-size snapshot if one exists, otherwise a
                // valid centered fallback. Preserve Maximized in either case.
                _normalPlacement=WindowPlacementStore.Load(_configPath);
                if(_normalPlacement is null)
                {
                    var screen=CurrentDisplay();
                    var wa=screen?.WorkingArea;
                    var scale=screen?.Scaling??1.0;
                    var left=wa is null?Position.X:wa.Value.X+
                        (wa.Value.Width-(int)Math.Round(DefaultWindowWidth*scale))/2;
                    var top=wa is null?Position.Y:wa.Value.Y+
                        (wa.Value.Height-(int)Math.Round(MinHeight*scale))/2;
                    _normalPlacement=new WindowPlacement(left,top,DefaultWindowWidth,MinHeight);
                }
                PersistNormalPlacement();
            }
            else CaptureNormalPlacement();
        }
    }

    private Avalonia.Platform.Screen? CurrentDisplay()
    {
        try{return Screens.ScreenFromWindow(this)??Screens.Primary??Screens.All.FirstOrDefault();}
        catch(InvalidOperationException){return null;}
    }

    private void BeginProgrammaticPlacement()
    {
        // Ignore delayed Avalonia SizeChanged/PositionChanged notifications during
        // a restore. Without this guard, the previous minimum-sized Bounds can
        // overwrite a previously saved larger window after Opened or tray reopen.
        _settlingPlacement=true;
        _placementRestoreTimer.Stop();
        _placementSaveTimer.Stop();
        _pendingPlacement=null;
        _captureAfterSettle=false;
        _applyingPlacement=true;
    }

    private void EndProgrammaticPlacement()
    {
        _applyingPlacement=false;
        _placementRestoreTimer.Start();
    }

    private void CenterAtDefaultSize()
    {
        BeginProgrammaticPlacement();
        try
        {
            WindowState=WindowState.Normal;
            _rememberedMaximized=false;
            _maximizeOnOpening=false;
            Width=DefaultWindowWidth;Height=MinHeight;
            var display=CurrentDisplay();
            if(display is not null)
            {
                var work=display.WorkingArea;
                var scale=display.Scaling;
                var pixelWidth=(int)Math.Round(DefaultWindowWidth*scale);
                var pixelHeight=(int)Math.Round(MinHeight*scale);
                WindowStartupLocation=WindowStartupLocation.Manual;
                Position=new PixelPoint(work.X+(work.Width-pixelWidth)/2,
                    work.Y+(work.Height-pixelHeight)/2);
            }
            if(_rememberWindowPosition)
                _normalPlacement=new WindowPlacement(Position.X,Position.Y,DefaultWindowWidth,MinHeight);
        }
        finally{EndProgrammaticPlacement();}
    }

    private bool RestoreWindowPlacement(WindowPlacement saved)
    {
        // Validate monitor connectivity and scaling before applying the saved
        // physical position and logical size. Do not strand a window off-screen.
        Avalonia.Platform.Screen? display=null;
        try
        {
            display=Screens.All.FirstOrDefault(screen=>
            {
                var wa=screen.WorkingArea;
                var width=saved.Width*screen.Scaling;
                var height=saved.Height*screen.Scaling;
                return Math.Min(saved.X+width,wa.Right)-Math.Max(saved.X,wa.X)>=100 &&
                       Math.Min(saved.Y+height,wa.Bottom)-Math.Max(saved.Y,wa.Y)>=100;
            });
        }
        catch(InvalidOperationException){return false;}
        if(display is null && saved.Maximized)
        {
            // A maximized window must not disappear when its former monitor
            // was disconnected. Maximize on an available display instead.
            display=CurrentDisplay();
            if(display is not null)
            {
                var area=display.WorkingArea;
                var scale=display.Scaling;
                saved=new WindowPlacement(
                    area.X+(area.Width-(int)Math.Round(DefaultWindowWidth*scale))/2,
                    area.Y+(area.Height-(int)Math.Round(MinHeight*scale))/2,
                    DefaultWindowWidth,MinHeight,true);
            }
        }
        if(display is null)return false;
        var work=display.WorkingArea;
        var widthDip=Math.Clamp(saved.Width,MinWidth,Math.Max(MinWidth,work.Width/display.Scaling));
        var heightDip=Math.Clamp(saved.Height,MinHeight,Math.Max(MinHeight,work.Height/display.Scaling));
        var widthPx=(int)Math.Round(widthDip*display.Scaling);
        var heightPx=(int)Math.Round(heightDip*display.Scaling);
        BeginProgrammaticPlacement();
        try
        {
            WindowState=WindowState.Normal;
            _rememberedMaximized=saved.Maximized;
            _maximizeOnOpening=saved.Maximized;
            Width=widthDip;Height=heightDip;
            WindowStartupLocation=WindowStartupLocation.Manual;
            Position=new PixelPoint(
                Math.Clamp(saved.X,work.X,Math.Max(work.X,work.Right-widthPx)),
                Math.Clamp(saved.Y,work.Y,Math.Max(work.Y,work.Bottom-heightPx)));
            // Keep the known-good requested dimensions until an ACTUAL user
            // resize/move has settled. Never read the previous Bounds right here.
            _normalPlacement=new WindowPlacement(Position.X,Position.Y,widthDip,heightDip,saved.Maximized);
        }
        finally{EndProgrammaticPlacement();}
        return true;
    }

    private void CaptureNormalPlacement()
    {
        if(!_placementReady || !_rememberWindowPosition || _applyingPlacement ||
            _hidingWindow || _settlingPlacement || !IsVisible || WindowState!=WindowState.Normal)return;
        var width=Bounds.Width;
        var height=Bounds.Height;
        if(!double.IsFinite(width) || !double.IsFinite(height) ||
            width<MinWidth-2 || height<MinHeight-2)return;
        // Debounce native Window size/move events. Only a stable normal-window
        // rectangle is committed. Intermediate minimize/restore geometry cannot
        // go directly into the persistent file.
        _pendingPlacement=new WindowPlacement(Position.X,Position.Y,
            Math.Max(MinWidth,width),Math.Max(MinHeight,height));
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void CommitPendingPlacement()
    {
        if(!_rememberWindowPosition || _pendingPlacement is null)return;
        _normalPlacement=_pendingPlacement;
        _pendingPlacement=null;
        PersistNormalPlacement();
    }

    private void PersistNormalPlacement()
    {
        if(!_rememberWindowPosition || _normalPlacement is null)return;
        try{WindowPlacementStore.Save(_configPath,
            _normalPlacement with {Maximized=_rememberedMaximized});}
        catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
    }

    internal void SuspendPlacementForHide()
    {
        FlushWindowPlacement();
        // Stop treating ShowInTaskbar=false / Hide() native events as user restores.
        _hidingWindow=true;
        _placementSaveTimer.Stop();
        _placementRestoreTimer.Stop();
    }

    internal void ResumePlacementAfterHide()=>_hidingWindow=false;

    internal void FlushWindowPlacement()
    {
        _placementSaveTimer.Stop();
        if(!_rememberWindowPosition)return;
        // WindowState is still Maximized on [X]; on [-] it is already Minimized.
        // Neither path must downgrade an existing maximized preference.
        if(WindowState==WindowState.Maximized)_rememberedMaximized=true;
        // [X] and [-] both flush the last rectangle observed *before* hiding.
        // Do NOT sample Bounds during Closing: Windows may have already changed
        // the native frame to an intermediate/minimized size at that point.
        if(_pendingPlacement is not null)CommitPendingPlacement();
        else PersistNormalPlacement();
    }

    // Same Card Print method: images are always native-pixel, selected by
    // Avalonia's actual light/dark content theme and the window RenderScaling.
    private void RefreshBrandArtworks()
    {
        var white=ActualThemeVariant==ThemeVariant.Dark;
        if(_headerBrandImage is not null)BrandArtwork.Apply(_headerBrandImage,white,RenderScaling,48);
        if(_aboutBrandImage is not null)BrandArtwork.Apply(_aboutBrandImage,white,RenderScaling,32);
    }
    internal void SetTaskbarBrandStatus(BrandStatus status,bool force=false)
    {
        _taskbarStatus=status;
        _taskbarStatusIcons.Apply(this,status,force);
    }
    internal void RefreshTaskbarBrandStatus()=>SetTaskbarBrandStatus(_taskbarStatus,force:true);
    private Border Card(Control inner)
    {
        return UiKit.Card(inner,Dark);
    }
    private StackPanel Column(int spacing=14)=>UiKit.Stack(spacing);
    private TextBlock Text(string t,int size=14,bool bold=false,IBrush? brush=null)
        =>UiKit.Text(t,size,bold,brush??Fore);
    private TextBlock Muted(string t,int size=13)=>Text(t,size,false,Secondary);
    private Button Action(string text,Action action,bool primary=false,string? icon=null)=>UiKit.Button(text,action,primary,icon);
    private StackPanel Row(params Control[] controls)
    {
        var r=UiKit.Stack(10,Orientation.Horizontal);
        foreach(var x in controls)r.Children.Add(x);
        return r;
    }
    private Border Section(string title,Control inside,string? subtitle=null)
    {
        var stack=Column(13);
        stack.Children.Add(Text(title,18,true));
        if(subtitle is not null)stack.Children.Add(Muted(subtitle));
        stack.Children.Add(inside);
        return Card(stack);
    }
    // Fill equal-height cards and anchor each action group to the bottom-left.
    private Border CardWithFooter(Control body,Control footer,bool compact=false)
    {
        var grid=new Grid{RowDefinitions=new RowDefinitions("*,Auto"),RowSpacing=compact?10:16};
        grid.Children.Add(body);
        Grid.SetRow(footer,1);
        footer.HorizontalAlignment=HorizontalAlignment.Left;
        grid.Children.Add(footer);
        return Card(grid);
    }
    private Border SectionWithFooter(string title,Control body,Control footer,string? subtitle=null)
    {
        var content=Column(13);
        content.Children.Add(Text(title,18,true));
        if(subtitle is not null)content.Children.Add(Muted(subtitle));
        content.Children.Add(body);
        return CardWithFooter(content,footer);
    }
    private Control Wrap(Control body)
    {
        return new ScrollViewer{Content=new Border{Child=body,Padding=new Thickness(28,23,28,28)},
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    private void BuildShell()
    {
        Background=new SolidColorBrush(BackgroundColor);
        var root=new DockPanel();
        // The light header must be light: the reference uses a black in-window
        // icon for light content themes. Do not add a logo backing rectangle.
        var header=new Grid{Height=76,Background=new SolidColorBrush(Color.Parse(Dark?"#172738":"#E9F1F8")),
            ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        // Icon and two left-aligned text lines form one centered, compact brand row.
        // The 48-DIP icon matches the combined height of the title and subtitle.
        var brand=Row();
        brand.Spacing=12;
        brand.Margin=new Thickness(24,0,0,0);
        brand.VerticalAlignment=VerticalAlignment.Center;
        brand.HorizontalAlignment=HorizontalAlignment.Left;
        _headerBrandImage=new Image{VerticalAlignment=VerticalAlignment.Center,Stretch=Stretch.Uniform};
        BrandArtwork.Apply(_headerBrandImage,ActualThemeVariant==ThemeVariant.Dark,RenderScaling,48);
        brand.Children.Add(_headerBrandImage);
        var title=Column(1);
        title.VerticalAlignment=VerticalAlignment.Center;
        title.HorizontalAlignment=HorizontalAlignment.Left;
        var name=UiKit.Text("KeyPassXC Cloud Sync",23,true,Dark?Brushes.White:Fore);
        name.TextWrapping=TextWrapping.NoWrap;
        title.Children.Add(name);
        var description=UiKit.Text("Безопасная синхронизация базы KeePassXC",12,false,
            new SolidColorBrush(Color.Parse(Dark?"#AEC8DF":"#556E85")));
        description.TextWrapping=TextWrapping.NoWrap;
        title.Children.Add(description);
        brand.Children.Add(title);
        header.Children.Add(brand);
        var themeRow=Row(UiKit.Text(UiLang.T("Тема:"),13,false,Dark?Brushes.White:Fore));
        themeRow.Margin=new Thickness(0,0,27,0);
        themeRow.VerticalAlignment=VerticalAlignment.Center;
        // Language codes and theme values remain invariant in KeePassSync.ini.
        var themeItems=new[] {"Системная","Светлая","Тёмная"}
            .Select(x=>new ComboBoxItem{Tag=x,Content=UiLang.ThemeName(x)}).ToArray();
        var theme=new ComboBox{Width=132,ItemsSource=themeItems,
            SelectedItem=themeItems.First(x=>(string)x.Tag! == _draft.Theme),MinHeight=34};
        themeRow.Children.Add(theme);
        theme.SelectionChanged+=(_,_)=>
        {
            if(theme.SelectedItem is not ComboBoxItem option || option.Tag is not string newTheme || newTheme==_draft.Theme)return;
            _draft.Theme=newTheme;
            _app.SetTheme(newTheme);
        };
        themeRow.Children.Add(UiKit.Text(UiLang.T("Язык:"),13,false,Dark?Brushes.White:Fore));
        // Show full language names in both the dropdown and the selected value.
        var languageItems=new[]
        {
            new LanguageChoice("auto",UiLang.T("Авто"),UiLang.T("Авто")),
            new LanguageChoice("ru","Русский","Русский"),
            new LanguageChoice("uk","Українська","Українська"),
            new LanguageChoice("en","English","English")
        };
        var language=new ComboBox
        {
            Width=142,MinHeight=34,ItemsSource=languageItems,
            SelectedItem=languageItems.First(x=>x.Code==_draft.Language),
            // Never abbreviate the chosen language in the selector.
            ItemTemplate=new FuncDataTemplate<LanguageChoice>((item,_)=>
                new TextBlock{Text=item.Full,MinWidth=125}),
            SelectionBoxItemTemplate=new FuncDataTemplate<LanguageChoice>((item,_)=>
                new TextBlock{Text=item.Short})
        };
        themeRow.Children.Add(language);
        language.SelectionChanged+=(_,_)=>
        {
            if(language.SelectedItem is not LanguageChoice selected || selected.Code==_draft.Language)return;
            var code=selected.Code;
            var previous=_draft.Language;
            try
            {
                SettingsStore.SaveUiLanguage(_configPath,code);
                _draft.Language=code;
                UiLang.Use(code);
                Dispatcher.UIThread.Post(() =>
                {
                    BuildShell();
                    _app.RefreshTrayLanguage();
                });
            }
            catch(Exception e)
            {
                _draft.Language=previous;
                ShowProblem("Настройки",e.Message);
            }
        };
        Grid.SetColumn(themeRow,1);header.Children.Add(themeRow);
        DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        // Center footer text vertically; no top margin that pushes the version
        // down against the bottom edge on high-DPI displays.
        var footer=new Grid{Height=34,ColumnDefinitions=new ColumnDefinitions("*,Auto"),
            Background=new SolidColorBrush(Color.Parse(Dark?"#172432":"#EAF0F7"))};
        var footerHint=new TextBlock
        {
            Text=UiLang.T("Все аккаунты работают независимо · закрытие окна не останавливает синхронизацию"),
            Foreground=Secondary,FontSize=UiTypography.FootnoteSize,
            Margin=new Thickness(26,0,0,0),VerticalAlignment=VerticalAlignment.Center,
            IsVisible=false // Show only after measuring sufficient footer width.
        };
        footer.Children.Add(footerHint);
        // The global status bar stays concise on every page; credits belong
        // exclusively to the About page.
        var v=Text(BuildInfo.Version+" · "+DesktopPlatform.Current.PlatformName,12,false,Secondary);
        v.Margin=new Thickness(0,0,25,0);
        v.VerticalAlignment=VerticalAlignment.Center;
        v.TextAlignment=TextAlignment.Right;
        v.TextWrapping=TextWrapping.NoWrap;
        Grid.SetColumn(v,1);footer.Children.Add(v);
        footer.SizeChanged+=(_,e)=>footerHint.IsVisible=e.NewSize.Width>=1450;
        var footerBorder=new Border
        {
            BorderBrush=new SolidColorBrush(Color.Parse(Dark?"#2A3C4E":"#C9D7E6")),
            BorderThickness=new Thickness(0,1,0,0),Child=footer
        };
        DockPanel.SetDock(footerBorder,Dock.Bottom);root.Children.Add(footerBorder);
        var pages=new Grid{RowDefinitions=new RowDefinitions("Auto,*")};
        // Match Card Print's MainWindow.axaml: a thin KpBorderBrush frame
        // surrounds the tabs AND their content, with a small inset from the
        // header. The TabItems remain adjacent, without extra separator lines.
        var tabPalette=ReferenceTabPalette.For(Dark);
        var navigation=new Grid
        {
            Name="MainNavigationTabs",
            ColumnDefinitions=new ColumnDefinitions("*,Auto"),
            MinHeight=29,
            Background=new SolidColorBrush(tabPalette.Window)
        };
        var mainTabs=UiKit.Stack(0,Orientation.Horizontal);
        var aboutTabs=UiKit.Stack(0,Orientation.Horizontal);
        navigation.Children.Add(mainTabs);
        Grid.SetColumn(aboutTabs,1);navigation.Children.Add(aboutTabs);
        string[] headings=["Состояние","Облака","Компьютеры","Настройки","Диагностика","История","О программе"];
        string[] navIcons=["database","cloud","devices","settings","check","history","info"];
        _tabButtons=new TabItem[headings.Length];
        for(var i=0;i<headings.Length;i++)
        {
            var index=i;
            var tab=new TabItem
            {
                Header=UiKit.IconLabel(headings[i],navIcons[i],navigation:true),
                FontSize=UiTypography.NavigationSize,Height=29,MinHeight=29,Padding=new Thickness(11,5),
                HorizontalContentAlignment=HorizontalAlignment.Center,
                VerticalContentAlignment=VerticalAlignment.Center
            };
            tab.Classes.Add("cloud-sync-nav-tab");
            tab.PointerPressed+=(_,e)=>
            {
                if(e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed)
                {
                    NavigateTo(index);
                    e.Handled=true;
                }
            };
            tab.KeyDown+=(_,e)=>
            {
                if(e.Key is Key.Enter or Key.Space)
                {
                    NavigateTo(index);
                    e.Handled=true;
                }
            };
            _tabButtons[i]=tab;
            (i==headings.Length-1?aboutTabs:mainTabs).Children.Add(tab);
        }
        pages.Children.Add(navigation);
        _pageContent=new ContentControl { Name="MainPageContentHost" };
        Grid.SetRow(_pageContent,1);
        pages.Children.Add(_pageContent);
        // This is the same single-pixel enclosing Border used by Card Print
        // around MainTabs. Its TOP edge must be visible across the full tab
        // strip; the 8-DIP outer margin supplies the gap below the header.
        var tabAreaFrame=new Border
        {
            Name="MainContentFrame",
            Margin=new Thickness(8),
            BorderBrush=new SolidColorBrush(tabPalette.Border),
            BorderThickness=new Thickness(1),
            Child=pages
        };
        root.Children.Add(tabAreaFrame);
        Content=root;
        _page=Math.Clamp(_page,0,_tabButtons.Length-1);
        UpdateNavigation();
        RefreshCurrentPage();
    }
    internal string DatabaseName=>_draft.LocalFile.Length==0?"База не выбрана":Path.GetFileName(_draft.LocalFile);
    internal void RefreshTheme(){BuildShell();RefreshBrandArtworks();}
    private void UpdateNavigation()
    {
        // TabItem:selected and TabItem:pointerover come directly from
        // Card Print's Compact.axaml; no application-specific color logic.
        for(var i=0;i<_tabButtons.Length;i++)
            _tabButtons[i].IsSelected=i==_page;
    }
    internal void NavigateTo(int index)
    {
        _page=Math.Clamp(index,0,_tabButtons.Length-1);
        UpdateNavigation();
        RefreshCurrentPage();
    }
    private void RefreshCurrentPage()
    {
        if(_pageContent is null)return;
        _liveStatus=null;
        _statusHeadline=null;
        _pollCountdownLabels.Clear();
        _cloudActivityLabels.Clear();
        _cloudStateLabels.Clear();
        _diagnosticsLogScroll=null;
        _diagnosticsRows=null;
        _diagnosticsRenderedCount=0;
        _diagnosticsStatus=null;
        _diagnosticsProgress=null;
        _diagnosticsActions=[];
        if(_page!=5)CancelProtectedDataScan();
        _pageContent.Content=_page switch
        {
            0=>BuildOverview(),1=>BuildClouds(),2=>BuildComputers(),3=>BuildSettings(),4=>BuildDiagnostics(),5=>BuildHistory(),_=>BuildAbout()
        };
    }
    internal void RefreshLiveStatus()
    {
        if(_liveStatus is not null)
        {
            _liveStatus.Text=UiLang.T(OverviewProgressLabel());
            ToolTip.SetTip(_liveStatus,_liveStatus.Text);
        }
        if(_statusHeadline is not null)
        {
            _statusHeadline.Text=UiLang.T(StatusLabel());
            ToolTip.SetTip(_statusHeadline,_statusHeadline.Text);
        }
        foreach(var (label,cloudId) in _pollCountdownLabels)
            label.Text=UiLang.T(CloudCountdown(cloudId));
        foreach(var (label,cloudId,timeout) in _cloudActivityLabels)
            label.Text=UiLang.T(CloudActivityLine(cloudId,timeout));
        foreach(var (label,cloudId,idleState) in _cloudStateLabels)
            label.Text=UiLang.T(_app.Engine?.Activity.Get(cloudId)?.Active==true
                ?"Синхронизация…":CloudStatusLabel(idleState));
        RefreshDiagnosticsActivity();
    }
    internal void UpdateStatus(AppState state)
    {
        var conflictChanged=_state?.Conflict!=state.Conflict ||
            !(_state?.ConflictVersionIds??[]).SequenceEqual(state.ConflictVersionIds);
        _state=state;
        // History statistics are explicitly refreshed by the user: do not repeatedly
        // cancel a background filesystem scan on every engine status update.
        if(_page is 0 or 1 or 2 || (_page==5 && conflictChanged))RefreshCurrentPage();
    }
    internal void ShowProblem(string heading,string message)
    {
        _app.OpenWindow();
        _=MessageDialog.Info(this,heading,UiErrorDisplay.Present(message));
    }
    internal void ReloadSharedConfiguration()=>ReloadDraft();
}
