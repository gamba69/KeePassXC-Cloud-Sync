using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.LogicalTree;
using KeyPassXCCloudSync;

// This is an isolated headless UI-structure smoke test. No KDBX, secrets,
// cloud network access, or background synchronization is ever started.
// Initialize Avalonia on this STA thread: avoid the separate test session,
// which creates a second dispatch thread and may hang at Dispatch/Dispose.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var directory=Path.Combine(Path.GetTempPath(),"kpcs-ui-smoke-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(directory,"smoke.ini");
        ShellWindow? shell=null;
        try
        {
            Directory.CreateDirectory(directory);
            DesktopSmokeAccess.ConfigurePlatform();
            // Pixel-size assertions require the real Skia decoder. The default
            // headless drawing backend is a fake that does not render pixels.
            AppBuilder.Configure<GuiApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = false
                })
                .UseHarfBuzz()
                .SetupWithoutStarting();
            Pass("headless initialized");

            var app=Application.Current as GuiApp
                ?? throw new Exception("GuiApp was not initialized");
            Check("sync engine not started",!app.HasRunningEngine);
            // A second launch must signal the existing process, acknowledge
            // same-version activation and refuse to start a duplicate engine.
            if(OperatingSystem.IsWindows())
            {
                var coordinator=new WindowsInstanceCoordinator();
                var instanceConfig=Path.Combine(directory,"single-instance-test.ini");
                var activationCount=0;
                using(var primary=coordinator.Enter(instanceConfig,
                    ()=>Interlocked.Increment(ref activationCount)))
                using(var secondary=coordinator.Enter(instanceConfig,()=>{}))
                {
                    Check("first instance is primary",primary.IsPrimary);
                    Check("duplicate instance is not primary",!secondary.IsPrimary);
                    Check("duplicate launch receives activation acknowledgement",
                        secondary.ActivateExisting());
                    Check("duplicate launch signalled primary",activationCount==1);
                }
                Pass("Windows single-instance launch and acknowledgement");
            }
            // Exercise the actual Windows resolver without launching any CLI.
            // The on-disk executables are inert test markers.
            if(OperatingSystem.IsWindows())
            {
                var appFolder=Path.Combine(directory,"portable-app");
                var pathFolder=Path.Combine(directory,"on PATH");
                var alternateFolder=Path.Combine(directory,"explicit");
                Directory.CreateDirectory(appFolder);
                Directory.CreateDirectory(pathFolder);
                Directory.CreateDirectory(alternateFolder);
                var localCli=Path.Combine(appFolder,"proton-drive.exe");
                var pathCli=Path.Combine(pathFolder,"proton-drive.exe");
                var manualCli=Path.Combine(alternateFolder,"proton-drive.exe");
                File.WriteAllText(pathCli,"");
                File.WriteAllText(manualCli,"");
                var iniPath=Path.Combine(appFolder,"config.ini");
                Check("Proton CLI found through PATH when not beside EXE",
                    ProtonCliLocator.Resolve("",appFolder,iniPath,pathFolder)==pathCli);
                File.WriteAllText(localCli,"");
                Check("Proton CLI beside app takes priority over PATH",
                    ProtonCliLocator.Resolve("",appFolder,iniPath,pathFolder)==localCli);
                Check("explicit Proton CLI path takes priority over both",
                    ProtonCliLocator.Resolve(manualCli,appFolder,iniPath,pathFolder)==manualCli);
                File.Delete(localCli);
                var toolsFolder=Path.Combine(appFolder,"tools");
                Directory.CreateDirectory(toolsFolder);
                var toolsCli=Path.Combine(toolsFolder,"proton-drive.exe");
                File.WriteAllText(toolsCli,"");
                Check("portable tools directory is checked before PATH",
                    ProtonCliLocator.Resolve("",appFolder,iniPath,pathFolder)==toolsCli);
                File.Delete(toolsCli);
                Check("missing explicit Proton CLI never silently switches to PATH",
                    ProtonCliLocator.Resolve(Path.Combine(alternateFolder,"missing.exe"),
                        appFolder,iniPath,pathFolder)==Path.Combine(alternateFolder,"missing.exe"));
                Check("quoted PATH entries with spaces are supported",
                    ProtonCliLocator.Resolve("",appFolder,iniPath,"\""+pathFolder+"\"")==pathCli);
                Pass("Proton CLI resolution: portable, PATH and explicit file");
            }
            Check("fresh install (no INI)",!File.Exists(path));
            shell=new ShellWindow(app,path);
            // UI-structure tests need the logical tree, not a native/window-host
            // lifecycle. Showing and then closing a window without a running
            // dispatcher can leave the host with a nonzero process exit status
            // AFTER all assertions have succeeded. Keep this window unshown.
            Check("main shell",shell.Content is DockPanel);
            Check("headless window is not displayed",!shell.IsVisible);
            Pass("main shell created");
            Check("dark and light zebra brushes resolve from application resources",
                app.TryGetResource("KpZebraBrush",ThemeVariant.Dark,out var darkStripe) &&
                darkStripe is SolidColorBrush darkBrush &&
                darkBrush.Color==Color.Parse("#1D3246") &&
                app.TryGetResource("KpZebraBrush",ThemeVariant.Light,out var lightStripe) &&
                lightStripe is SolidColorBrush lightBrush &&
                lightBrush.Color==Color.Parse("#EEF3F8"));
            Pass("theme-specific zebra brushes");
            // Awaiting protocol reconciliation and an active upload are normal.
            var normalClouds=new List<ConfiguredCloudStatus>
            {
                new("A",new ReplicaStatus{State="BEHIND"}),
                new("B",new ReplicaStatus{State="SYNCING"}),
                new("C",new ReplicaStatus{State="SYNCED"})
            };
            Check("reconciling is healthy",CloudHealthPresentation.IsHealthy("BEHIND"));
            Check("in-flight synchronization is healthy",CloudHealthPresentation.IsHealthy("SYNCING"));
            Check("PARTIAL without failed providers does not flash yellow",
                CloudHealthPresentation.Severity(new AppState{Result="PARTIAL"},normalClouds,false)
                    ==BrandStatus.Normal);
            Check("SYNCING with pending providers remains normal",
                CloudHealthPresentation.Severity(new AppState{Result="SYNCING"},normalClouds,false)
                    ==BrandStatus.Normal);
            normalClouds[1]=new("B",new ReplicaStatus{State="OFFLINE"});
            Check("actual offline provider still warns",
                CloudHealthPresentation.Severity(new AppState{Result="PARTIAL"},normalClouds,false)
                    ==BrandStatus.Warning);
            Check("conflicts remain critical",
                CloudHealthPresentation.Severity(new AppState{Result="CONFLICT"},normalClouds,false)
                    ==BrandStatus.Error);
            Pass("cloud health states and stable tray severity");

            // A saved last-stage notification is not evidence that a provider
            // is still running after its live activity has ended.
            Check("completed scan no longer claims to check Proton",
                ShellWindow.NormalizeOverviewProgress("CHECK",
                    "Проверяю изменения в Proton","PARTIAL")=="Ожидаю следующей проверки");
            Check("completed scan with confirmed head shows idle",
                ShellWindow.NormalizeOverviewProgress("CHECK",
                    "Проверяю изменения в Proton","SYNCED")=="Ожидаю следующей проверки");
            Check("completed scan with conflict asks for resolution",
                ShellWindow.NormalizeOverviewProgress("CHECK",
                    "Проверяю изменения в Proton","CONFLICT")==
                    "Требуется разрешение конфликта");
            Check("stale KDBX transfer detail is also retired",
                ShellWindow.NormalizeOverviewProgress("SYNC",
                    "Передана KDBX: Proton…","SYNCED")=="Ожидаю следующей проверки");
            Check("actual host preparation survives",
                ShellWindow.NormalizeOverviewProgress("CHECK",
                    "Проверяю локальную базу и состояние облаков…","PARTIAL")==
                    "Проверяю локальную базу и состояние облаков…");
            Pass("overview progress never retains a completed provider name");

            // Idle text follows the persisted provider retry deadline, not
            // the host maintenance timer or a stale CHECK/SYNC callback.
            var now=new DateTimeOffset(2026,11,11,12,0,0,TimeSpan.Zero);
            var clouds=new (string Name,string CloudId)[]
                {("Home Assistant","ha"),("Google","google"),("Proton","proton")};
            var idle=new AppState
            {
                Result="PARTIAL",
                Clouds=new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Home Assistant"]=new ReplicaStatus{State="ERROR",Error="HTTP 523"},
                    ["Google"]=new ReplicaStatus{State="SYNCED"},
                    ["Proton"]=new ReplicaStatus{State="SYNCED"}
                },
                NextCloudPollUtc=new(StringComparer.OrdinalIgnoreCase)
                {
                    ["ha"]=now.AddSeconds(44),
                    ["google"]=now.AddMinutes(3),
                    ["proton"]=now.AddMinutes(2)
                }
            };
            UiLang.Use("ru");
            Check("failed cloud retry names the provider and live deadline",
                ShellWindow.OverviewIdleLine(idle,clouds,now)==
                "Повторная попытка: Home Assistant · через 00:44");
            Check("retry countdown follows the clock",
                ShellWindow.OverviewIdleLine(idle,clouds,now.AddSeconds(14))==
                "Повторная попытка: Home Assistant · через 00:30");
            idle.Clouds["Proton"].State="ERROR";
            Check("multiple failed clouds prioritize the earliest retry",
                ShellWindow.OverviewIdleLine(idle,clouds,now)==
                "Повторная попытка: Home Assistant · через 00:44");
            idle.Clouds["Proton"].State="SYNCED";
            idle.NextCloudPollUtc.Remove("ha");
            Check("unknown retry time is never fabricated",
                ShellWindow.OverviewIdleLine(idle,clouds,now)==
                "Повторная попытка: Home Assistant · время следующей попытки неизвестно");
            idle.NextCloudPollUtc["ha"]=now.AddSeconds(44);
            Check("expired deadline does not claim a new network request",
                ShellWindow.OverviewIdleLine(idle,clouds,now.AddMinutes(1))==
                "Повторная попытка: Home Assistant · ожидает запуска");
            idle.Clouds["Home Assistant"].State="SYNCED";
            idle.Result="SYNCED";
            Check("healthy idle uses the next scheduled provider poll",
                ShellWindow.OverviewIdleLine(idle,clouds,now)==
                "Следующая проверка: Home Assistant · через 00:44");
            idle.Clouds["Home Assistant"].State="BEHIND";
            idle.Result="PARTIAL";
            Check("protocol reconciliation is not labelled a retry",
                ShellWindow.OverviewIdleLine(idle,clouds,now)==
                "Ожидание согласования: Home Assistant");
            idle.Clouds["Home Assistant"].State="NEEDS_AUTH";
            Check("authorization issue cannot be represented as an automatic retry",
                ShellWindow.OverviewIdleLine(idle,clouds,now)==
                "Требуется авторизация: Home Assistant");
            idle.Clouds["Home Assistant"].State="SYNCED";
            idle.Conflict=true;
            Check("conflict requires manual resolution",
                ShellWindow.OverviewIdleLine(idle,clouds,now)==
                "Требуется разрешение конфликта");
            idle.Conflict=false;
            idle.Result="PARTIAL";
            Check("unconfirmed version is not called synchronized",
                ShellWindow.OverviewIdleLine(idle,clouds,now)==
                "Ожидание подтверждения версий · следующая проверка: Home Assistant · через 00:44");
            idle.Clouds["Home Assistant"].State="ERROR";
            UiLang.Use("en");
            Check("retry is translated to English without changing cloud names",
                ShellWindow.OverviewIdleLine(idle,clouds,now)==
                "Retry: Home Assistant · in 00:44");
            UiLang.Use("uk");
            Check("retry is translated to Ukrainian without changing cloud names",
                ShellWindow.OverviewIdleLine(idle,clouds,now)==
                "Повторна спроба: Home Assistant · через 00:44");
            UiLang.Use("auto");
            Pass("overview idle state reflects actual provider deadlines and cause");

            // Card Print 5.9.44: each bitmap is one original ICO frame,
            // and DIP dimensions adapt to DPI instead of resampling pixels.
            var logo=new Image();
            Check("new brand Image starts with Auto dimensions",
                double.IsNaN(logo.Width) && double.IsNaN(logo.Height));
            BrandArtwork.Apply(logo,false,1.0,48);
            Check("native 48px artwork decoded",logo.Source is Avalonia.Media.Imaging.Bitmap original &&
                original.PixelSize.Width==48 && original.PixelSize.Height==48);
            Check("new Auto-size Image receives 48 DIP dimensions",
                logo.Width==48 && logo.Height==48);
            var lightLogo=logo.Source;
            BrandArtwork.Apply(logo,true,1.0,48);
            Check("theme uses independent white native frame",!ReferenceEquals(lightLogo,logo.Source));
            Check("theme switch retains 48 DIP dimensions",
                logo.Width==48 && logo.Height==48);
            BrandArtwork.Apply(logo,true,1.25,48);
            Check("125 percent DPI selects native 64px frame",logo.Source is Avalonia.Media.Imaging.Bitmap scaled &&
                scaled.PixelSize.Width==64 && scaled.PixelSize.Height==64);
            Check("125 percent DPI adjusts DIP size without raster scaling",
                Math.Abs(logo.Width-51.2)<0.001 && Math.Abs(logo.Height-51.2)<0.001);
            BrandArtwork.Apply(logo,false,1.0,32);
            Check("About uses native 32px frame",logo.Source is Avalonia.Media.Imaging.Bitmap about &&
                about.PixelSize.Width==32 && about.PixelSize.Height==32 &&
                logo.Width==32 && logo.Height==32);
            // First application must also work when only one dimension is Auto.
            var halfAuto=new Image { Width=1 };
            BrandArtwork.Apply(halfAuto,false,1.0,48);
            Check("partially Auto-size brand image gets both native dimensions",
                halfAuto.Width==48 && halfAuto.Height==48);
            Pass("native DPI brand frames, theme switching and About art");
            CheckNavigationChrome(shell);
            Pass("Card Print tab frame, palette and centered footer");
            CheckAboutPresentation(shell);
            Pass("About: performance first, equal-height columns and percentile notation");
            shell.NavigateTo(0);
            CheckOverviewStatusBar(shell);
            Pass("Overview: full status text, readable actions and adaptive metadata");
            shell.NavigateTo(5);
            var historyRows=ActivePage(shell).GetLogicalDescendants().OfType<Grid>()
                .Where(x=>x.Name is not null &&
                    x.Name.StartsWith("ProtectedDataTableRow",StringComparison.Ordinal))
                .OrderBy(x=>x.Name,StringComparer.Ordinal).ToArray();
            Check("history data inventory stripes cover complete rows",
                historyRows.Length==4 && historyRows.All(row=>
                    row.ColumnDefinitions.Count==4 &&
                    row.HorizontalAlignment==Avalonia.Layout.HorizontalAlignment.Stretch &&
                    row.Children.OfType<Button>().Any(button=>Grid.GetColumn(button)==3)) &&
                historyRows[0].Background is SolidColorBrush &&
                historyRows[1].Background==Brushes.Transparent &&
                historyRows[2].Background is SolidColorBrush &&
                historyRows[3].Background==Brushes.Transparent);
            Pass("History: full-width, square-edged zebra-striped data rows");
            shell.NavigateTo(0);
            // Keep content cards as compact as Card Print settings-card (4 DIP).
            var referenceCard=UiKit.Card(new TextBlock());
            Check("Card Print content-card corners",
                referenceCard.CornerRadius.TopLeft==UiKit.ContentCardCornerRadius &&
                referenceCard.CornerRadius.TopRight==UiKit.ContentCardCornerRadius &&
                referenceCard.CornerRadius.BottomLeft==UiKit.ContentCardCornerRadius &&
                referenceCard.CornerRadius.BottomRight==UiKit.ContentCardCornerRadius &&
                UiKit.ContentCardCornerRadius==4);
            Pass("Card Print compact content cards");
            // Four cards at 1920 physical px / 125% DPI: content starts at
            // 40 px and ends before the scrollbar, without reducing type size.
            var compactWidth=ShellWindow.DashboardCardWidth;
            var compactGap=ShellWindow.DashboardCardSpacing;
            Check("four compact cards fit at 125% DPI",
                (4*compactWidth+4*compactGap)*1.25 <= 1920-2*40-20);
            Check("reduced minimum keeps two compact cards side by side",
                shell.MinWidth==ShellWindow.MinimumWindowWidth &&
                ShellWindow.MinimumWindowWidth==820 &&
                (2*compactWidth+2*compactGap) <= ShellWindow.MinimumWindowWidth-56-20);
            Check("startup retains previous spacious window width",
                ShellWindow.DefaultWindowWidth==910 &&
                shell.Width==ShellWindow.DefaultWindowWidth);
            // Window geometry must persist at the new minimum instead of being
            // silently rejected by the old 910-DIP reader.
            var geometryConfig=Path.Combine(directory,"geometry-test.ini");
            WindowPlacementStore.Save(geometryConfig,new WindowPlacement(24,36,820,640));
            var restoredGeometry=WindowPlacementStore.Load(geometryConfig);
            Check("narrow window geometry survives restart",
                restoredGeometry is {Width:820,Height:640});
            WindowPlacementStore.Save(geometryConfig,new WindowPlacement(24,36,819,640));
            Check("geometry below minimum is rejected",
                WindowPlacementStore.Load(geometryConfig) is null);
            Check("Google synced login is secondary",
                ShellWindow.LoginActionPresentation("SYNCED",true)==("Аккаунт",false));
            Check("Proton synced login is secondary",
                ShellWindow.LoginActionPresentation("SYNCED",false)==("Аккаунт",false));
            Check("auth failures demand prominent sign-in",
                ShellWindow.LoginActionPresentation("AUTH_ERROR",true)==("Войти",true) &&
                ShellWindow.LoginActionPresentation("NEEDS_AUTH",false)==("Войти",true));
            Check("offline does not falsely imply bad credentials",
                ShellWindow.LoginActionPresentation("OFFLINE",false)==("Аккаунт",false));
            Pass("compact four-column dashboard and auth button states");

            // Avalonia 12 changed TextBox.Watermark to PlaceholderText.
            UiLang.Use("en");
            var input=UiKit.Input("", "Имя подключения");
            Check("Avalonia 12 placeholder",input.PlaceholderText=="Connection name");
            var version=typeof(ShellWindow).Assembly.GetName().Version?.ToString(3)
                ?? throw new Exception("Desktop assembly version is missing");
            Check("window title",string.Equals(shell.Title,
                "KeyPassXC Cloud Sync "+version,StringComparison.Ordinal));
            // Font selection belongs to Avalonia; diagnostic-only typefaces are
            // centralized and do not require external downloaded font files.
            Check("diagnostic monospace configured",
                !string.IsNullOrWhiteSpace(UiTypography.DiagnosticMonospace.ToString()));
            Check("diagnostic symbols configured",
                !string.IsNullOrWhiteSpace(UiTypography.DiagnosticSymbols.ToString()));
            Check("diagnostic font sizing",
                UiTypography.DiagnosticTextSize > 0 &&
                UiTypography.DiagnosticSymbolSize >= UiTypography.DiagnosticTextSize);
            Pass("Avalonia 12 controls and typography");

            // Pinning managed packages alone is not enough: touching both
            // engines verifies the native Windows runtime assets load, too.
            // NuGet versions are pinned by project validation. CLR assembly
            // versions are not necessarily identical to NuGet package versions;
            // a numeric-major assertion would cause an unrelated false FAIL.
            Pass("SkiaSharp managed assembly " +
                typeof(SkiaSharp.SKFont).Assembly.GetName().Version);
            Pass("HarfBuzzSharp managed assembly " +
                typeof(HarfBuzzSharp.Buffer).Assembly.GetName().Version);
            using (var font = UiTypography.CreateSkiaFont(16))
            {
                Check("Skia 4 explicit typeface", font.Typeface != null);
                Check("Skia 4 measures text", font.MeasureText("Test Кирилиця 0O") > 0);
                Pass("SkiaSharp 4 native font and text measurement");
                // Verify a native canvas actually rasterizes the text; an
                // assembly-version check alone can miss an incompatible DLL.
                using var bitmap = new SkiaSharp.SKBitmap(256, 48);
                using var canvas = new SkiaSharp.SKCanvas(bitmap);
                using var paint = new SkiaSharp.SKPaint
                {
                    Color = SkiaSharp.SKColors.Black,
                    IsAntialias = true
                };
                canvas.Clear(SkiaSharp.SKColors.White);
                canvas.DrawText("Hello", 4f, 32f, SkiaSharp.SKTextAlign.Left, font, paint);
                var ink = false;
                for (var y = 0; y < bitmap.Height && !ink; y++)
                {
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        if (bitmap.GetPixel(x, y) != SkiaSharp.SKColors.White)
                        {
                            ink = true;
                            break;
                        }
                    }
                }
                Check("Skia 4 native text rasterization", ink);
                Pass("SkiaSharp 4 native rasterization");
                using var shaper = new SkiaSharp.HarfBuzz.SKShaper(SkiaSharp.SKTypeface.Default);
                var shaped = shaper.Shape("Привіт", font);
                Check("Skia 4 HarfBuzz text shaping",
                    shaped.Codepoints.Length > 0 && shaped.Width > 0);
                Pass("SkiaSharp 4 HarfBuzz shaping");
            }
            using (var buffer = new HarfBuzzSharp.Buffer())
            {
                Check("HarfBuzz native buffer created", buffer is not null);
                Pass("HarfBuzzSharp 14 native buffer");
            }
            Pass("SkiaSharp 4 and HarfBuzzSharp native font engines");

            foreach(var (code,heading) in new[]
            {
                ("ru","Состояние"),("uk","Стан"),("en","Status")
            })
            {
                UiLang.Use(code);
                shell.RefreshTheme();
                shell.NavigateTo(0);
                CheckPageHeading(shell,heading,"navigation "+code);
                Pass("locale "+code);
            }
            string[] pageHeadings=
                ["Состояние","Облака","Компьютеры","Настройки","Диагностика","История","KeyPassXC Cloud Sync"];
            foreach(var theme in new[]{ThemeVariant.Light,ThemeVariant.Dark})
            {
                app.RequestedThemeVariant=theme;
                shell.RefreshTheme();
                for(var page=0;page<pageHeadings.Length;page++)
                {
                    // Each page is built synchronously. Do not require a paint or
                    // dispatcher frame: this test checks the actual active UI tree.
                    shell.NavigateTo(page);
                    CheckPageHeading(shell,UiLang.T(pageHeadings[page]),
                        "page "+page+" under "+theme);
                    Pass(theme+" / page "+page);
                }
            }
            Check("history works without a saved INI",!File.Exists(path));
            Check("synchronization never started",!app.HasRunningEngine);
            Pass("first-run isolation");
        }
        catch(Exception ex)
        {
            TestReporter.Unhandled("UiSmokeTests",ex);
            Console.Error.Flush();
            return 1;
        }
        finally
        {
            // No ShellWindow.Show/Close: there is intentionally no dispatcher
            // message loop. Avoid enqueueing teardown work after assertions.
            // The unshown control tree is owned by this short-lived test process.
            shell=null;
            try {Directory.Delete(directory,true);}
            catch(IOException) {} catch(UnauthorizedAccessException) {}
        }
        // Only announce success AFTER the test and cleanup have completed.
        Console.WriteLine("[SUMMARY] UI smoke tests completed");
        Console.Out.Flush();
        return 0;
    }
    private static void CheckOverviewStatusBar(ShellWindow shell)
    {
        var page=ActivePage(shell);
        var banner=page.GetLogicalDescendants().OfType<Grid>()
            .FirstOrDefault(x=>x.Name=="OverviewStatusRow");
        var details=page.GetLogicalDescendants().OfType<Grid>()
            .FirstOrDefault(x=>x.Name=="OverviewStatusInfo");
        var statusCard=page.GetLogicalDescendants().OfType<Border>()
            .FirstOrDefault(x=>x.Name=="OverviewStatusCard");
        Check("overview stays within four cloud-card slots on ultrawide screens",
            statusCard is not null &&
            statusCard.MaxWidth==ShellWindow.OverviewStatusMaxWidth &&
            ShellWindow.OverviewStatusMaxWidth==
                4*ShellWindow.DashboardCardWidth+3*ShellWindow.DashboardCardSpacing &&
            ShellWindow.OverviewStatusMaxWidth==1430 &&
            statusCard.HorizontalAlignment==Avalonia.Layout.HorizontalAlignment.Left);
        Check("overview separates readable status, metadata and controls",
            banner is not null && banner.ColumnDefinitions.Count==3 &&
            banner.RowDefinitions.Count==2 && banner.ColumnSpacing==12 &&
            details is not null && details.ColumnDefinitions.Count==3 &&
            details.ColumnSpacing==12 &&
            details.ColumnDefinitions[0].Width.IsAuto &&
            details.ColumnDefinitions[1].Width.IsAuto &&
            details.ColumnDefinitions[2].Width.IsAuto &&
            details.MaxWidth==ShellWindow.OverviewMetadataMaxWidth &&
            details.HorizontalAlignment==Avalonia.Layout.HorizontalAlignment.Left);
        Check("minimum width places metadata below headline without hiding it",
            details is not null && Grid.GetRow(details)==1 &&
            Grid.GetColumn(details)==0 && Grid.GetColumnSpan(details)==3);
        var text=page.GetLogicalDescendants().OfType<TextBlock>().ToArray();
        foreach(var id in new[]{"OverviewStatusHeadline","OverviewStatusProgress"})
            Check("overview never ellipsizes important "+id,
                text.Any(label=>label.Name==id &&
                    label.TextWrapping==TextWrapping.Wrap &&
                    label.TextTrimming==TextTrimming.None));
        foreach(var id in new[]{"OverviewVaultCaption","OverviewCheckCaption",
            "OverviewChangeCaption","OverviewLastCheck","OverviewLastChange"})
            Check("overview retains "+id,
                text.Any(label=>label.Name==id &&
                    label.TextWrapping==TextWrapping.NoWrap &&
                    label.TextTrimming!=TextTrimming.CharacterEllipsis));
        Check("only the database path is shortened when necessary",
            text.Any(label=>label.Name=="OverviewVaultPath" &&
                label.TextTrimming==TextTrimming.PathSegmentEllipsis &&
                label.Width==ShellWindow.OverviewPathPreferredWidth &&
                ToolTip.GetTip(label) is string fullPath && fullPath==label.Text));
        var buttons=page.GetLogicalDescendants().OfType<Button>().ToArray();
        Check("overview toggle uses the same icon-and-text button size as sync",
            buttons.Any(b=>b.Name=="OverviewToggleSync" &&
                b.Height==ShellWindow.OverviewActionHeight &&
                b.Width==ShellWindow.OverviewActionWidth &&
                b.Content is StackPanel toggleContent &&
                toggleContent.Children.Count==2 &&
                toggleContent.Children[1] is TextBlock toggleLabel &&
                toggleLabel.Text==UiLang.T("Включить")));
        Check("overview action buttons keep identical dimensions",
            ShellWindow.OverviewActionWidth==188 && ShellWindow.OverviewActionHeight==40);
        Check("vault path width shrinks before metadata stacks",
            ShellWindow.OverviewAvailablePathWidth(1250,250)==260 &&
            ShellWindow.OverviewAvailablePathWidth(1200,250)==228 &&
            ShellWindow.OverviewAvailablePathWidth(1100,250)==128 &&
            !ShellWindow.StackOverviewMetadata(1100,250) &&
            !ShellWindow.StackOverviewMetadata(1072,250) &&
            ShellWindow.StackOverviewMetadata(1071,250) &&
            ShellWindow.StackOverviewMetadata(1200,390) &&
            !ShellWindow.StackOverviewMetadata(1220,390));
        Check("overview layout reserves full timestamps and button labels",
            ShellWindow.OverviewPathMinWidth==100 &&
            ShellWindow.OverviewTimestampReserveWidth==145 &&
            ShellWindow.OverviewActionWidth==188 &&
            banner is not null && banner.ColumnDefinitions[1].Width.IsStar);
    }

    private static void CheckNavigationChrome(ShellWindow shell)
    {
        if(shell.Content is not DockPanel root)
            throw new Exception("UI smoke failed: missing root DockPanel");
        var tabs=root.GetLogicalDescendants().OfType<TabItem>()
            .Where(b=>b.Classes.Contains("cloud-sync-nav-tab")).ToArray();
        Check("seven compact Card Print-style navigation tabs",tabs.Length==7 &&
            tabs.All(b=>b.MinHeight==29 && b.Padding.Left==11 &&
                b.Padding.Top==5 && b.FontSize==12));
        var tabGlyphs=tabs.Select(t=>(t.Header as StackPanel)?.Children
            .OfType<FluentIcons.Avalonia.FluentIcon>().FirstOrDefault()).ToArray();
        Check("compact 14-DIP navigation icons",tabGlyphs.Length==7 &&
            tabGlyphs.All(icon=>icon is not null &&
                icon.Width==UiKit.NavigationIconSize && icon.Height==UiKit.NavigationIconSize &&
                icon.IconSize==FluentIcons.Common.IconSize.Size20));
        // Check the exact source values, not an unrelated blue-accent treatment.
        var dark = ReferenceTabPalette.For(true);
        var light = ReferenceTabPalette.For(false);
        Check("Card Print dark tab palette",dark.Window.Equals(Color.Parse("#081524")) &&
            dark.Button.Equals(Color.Parse("#20384e")) &&
            dark.Surface.Equals(Color.Parse("#182b3f")) &&
            dark.Hover.Equals(Color.Parse("#243b56")) &&
            dark.Text.Equals(Color.Parse("#eef4fb")) &&
            dark.Border.Equals(Color.Parse("#31475f")));
        Check("Card Print light tab palette",light.Window.Equals(Color.Parse("#f3f4f7")) &&
            light.Button.Equals(Color.Parse("#e8edf4")) &&
            light.Surface.Equals(Color.Parse("#ffffff")) &&
            light.Hover.Equals(Color.Parse("#dbeafe")) &&
            light.Text.Equals(Color.Parse("#1c2730")) &&
            light.Border.Equals(Color.Parse("#ced4dc")));
        Check("only first tab selected",tabs[0].IsSelected &&
            tabs.Skip(1).All(t=>!t.IsSelected));
        shell.NavigateTo(1);
        Check("navigation changes selected tab",tabs[1].IsSelected &&
            tabs.Where((t,i)=>i!=1).All(t=>!t.IsSelected));
        shell.NavigateTo(0);
        var frame=root.Children.OfType<Border>()
            .FirstOrDefault(b=>b.Name=="MainContentFrame");
        Check("Card Print thin enclosing tab frame and header gap",
            frame is not null &&
            frame.BorderThickness.Left==1 && frame.BorderThickness.Top==1 &&
            frame.BorderThickness.Right==1 && frame.BorderThickness.Bottom==1 &&
            frame.Margin.Left==8 && frame.Margin.Top==8 &&
            frame.Margin.Right==8 && frame.Margin.Bottom==8 &&
            frame.BorderBrush is SolidColorBrush frameBrush &&
            (frameBrush.Color.Equals(dark.Border) || frameBrush.Color.Equals(light.Border)));
        var navigation=root.GetLogicalDescendants().OfType<Grid>()
            .FirstOrDefault(g=>g.Name=="MainNavigationTabs");
        Check("gapless native navigation inside framed content",navigation is not null &&
            navigation.MinHeight==29 && navigation.Margin.Left==0 &&
            navigation.Margin.Top==0 && navigation.Margin.Right==0 &&
            navigation.Margin.Bottom==0 &&
            navigation.Children.OfType<StackPanel>().Count()==2 &&
            navigation.Children.OfType<StackPanel>().All(p=>p.Spacing==0));
        var footer=root.Children.OfType<Border>().FirstOrDefault(b=>
            b.Child is Grid g && g.Height==34 && g.ColumnDefinitions.Count==2);
        Check("compact footer with two columns",footer is not null);
        var footerGrid=(Grid)footer!.Child!;
        var labels=footerGrid.GetLogicalDescendants().OfType<TextBlock>().ToArray();
        Check("footer text vertically centered",labels.Length==2 &&
            labels.All(t=>t.VerticalAlignment==Avalonia.Layout.VerticalAlignment.Center));
        var appVersion=typeof(ShellWindow).Assembly.GetName().Version?.ToString(3)??"unknown";
        Check("global footer shows version and platform only",
            labels.Count(t=>t.Text==appVersion+" · "+DesktopPlatform.Current.PlatformName)==1 &&
            labels.All(t=>(t.Text??string.Empty).IndexOf('©')<0 &&
                !(t.Text??string.Empty).Contains("OpenAI Codex",StringComparison.Ordinal) &&
                !(t.Text??string.Empty).Contains("github.com",StringComparison.Ordinal)) &&
            !footerGrid.GetLogicalDescendants().OfType<TextBlock>()
                .Any(t=>t.Name=="FooterRepositoryLink"));
    }
    private static void CheckAboutPresentation(ShellWindow shell)
    {
        shell.NavigateTo(6);
        var page=ActivePage(shell);
        var scroller=page.GetLogicalDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var sections=(scroller?.Content as StackPanel)?.Children;
        Check("performance appears immediately after About identity",
            sections is not null && sections.Count==3 &&
            sections[1].GetLogicalDescendants().OfType<TextBlock>()
                .Any(t=>new[]{"Производительность облачных подключений",
                    "Продуктивність хмарних підключень","Cloud connection performance"}
                    .Contains(t.Text,StringComparer.Ordinal)));
        var identity=sections?[0] as Grid;
        var aboutCredits=identity?.GetLogicalDescendants().OfType<WrapPanel>()
            .FirstOrDefault(p=>p.Name=="AboutCreditsRow");
        var creditLabels=aboutCredits?.Children.OfType<TextBlock>().ToArray() ?? [];
        var version=typeof(ShellWindow).Assembly.GetName().Version?.ToString(3)??"unknown";
        Check("About metadata row is directly under product title",
            page is Grid aboutGrid && aboutGrid.RowDefinitions.Count==0 &&
            sections is not null && sections.Count==3 && identity is not null &&
            identity.GetLogicalDescendants().OfType<StackPanel>().Any(intro=>
                intro.Children.Count==3 &&
                intro.Children[1]==aboutCredits &&
                intro.Children[0] is TextBlock title && title.Text=="KeyPassXC Cloud Sync"));
        Check("About metadata continues version, platform and runtime with credits",
            creditLabels.Any(t=>
                (t.Text??string.Empty).StartsWith(version+" · "+DesktopPlatform.Current.PlatformName+
                    " · "+DesktopPlatform.Current.ProcessArchitectureName+" · ",StringComparison.Ordinal) &&
                ((t.Text??string.Empty).Contains("Native AOT",StringComparison.Ordinal) ||
                    (t.Text??string.Empty).Contains("JIT",StringComparison.Ordinal)) &&
                (t.Text??string.Empty).Contains("© 2026 Igor К. Dulevich",StringComparison.Ordinal) &&
                (t.Text??string.Empty).Contains("Developed with AI assistance (OpenAI Codex)",StringComparison.Ordinal)));
        var repositoryLink=creditLabels.FirstOrDefault(t=>t.Name=="AboutRepositoryLink");
        Check("About header repository link looks like text, is focusable and points to GitHub",
            repositoryLink is not null && repositoryLink.Focusable &&
            (repositoryLink.TextDecorations is null || repositoryLink.TextDecorations.Count==0) &&
            repositoryLink.Text=="https://github.com/gamba69/KeePassXC-Cloud-Sync" &&
            (string?)ToolTip.GetTip(repositoryLink)==repositoryLink.Text &&
            aboutCredits is not null &&
            aboutCredits.Children.OfType<Button>().Count()==0 &&
            creditLabels.Length==2 &&
            Equals(creditLabels[0].Foreground,repositoryLink.Foreground));
        Check("About has no separate bottom credits strip",
            page is Grid aboutGrid2 && aboutGrid2.Children.Count==1 &&
            aboutGrid2.Children[0] is ScrollViewer);
        var details=page.GetLogicalDescendants().OfType<Grid>()
            .FirstOrDefault(g=>g.Name=="AboutDetailsGrid");
        var libraries=details?.Children.OfType<Control>()
            .FirstOrDefault(c=>c.Name=="AboutLibrariesCard");
        var environment=details?.Children.OfType<Control>()
            .FirstOrDefault(c=>c.Name=="AboutEnvironmentCard");
        var protocol=details?.Children.OfType<Control>()
            .FirstOrDefault(c=>c.Name=="AboutProtocolCard");
        Check("library spans the two right-hand About cards",
            details is not null && details.RowDefinitions.Count==2 &&
            libraries is not null && environment is not null && protocol is not null &&
            Grid.GetRowSpan(libraries)==2 &&
            Grid.GetColumn(environment)==1 && Grid.GetRow(environment)==0 &&
            Grid.GetColumn(protocol)==1 && Grid.GetRow(protocol)==1);
        // The performance note now explains the exact median first; keep
        // checking both the median and subscript-percentile notation without
        // depending on their order in the localized explanatory sentence.
        Check("performance explanation describes median and P₉₅ with correct notation",
            page.GetLogicalDescendants().OfType<TextBlock>()
                .Any(t=>t.Text is { } explanation &&
                    explanation.Contains("P₉₅",StringComparison.Ordinal) &&
                    !explanation.Contains('≤') &&
                    (explanation.Contains("Median",StringComparison.OrdinalIgnoreCase) ||
                     explanation.Contains("Медиан",StringComparison.OrdinalIgnoreCase) ||
                     explanation.Contains("Медіан",StringComparison.OrdinalIgnoreCase))));
        shell.NavigateTo(0);
    }
    private static Control ActivePage(ShellWindow shell)
    {
        if(shell.Content is not DockPanel root)
            throw new Exception("UI smoke failed: main window has no DockPanel root");
        var hosts=root.GetLogicalDescendants().OfType<ContentControl>()
            .Where(control=>control.Name=="MainPageContentHost").ToArray();
        if(hosts.Length!=1 || hosts[0].Content is not Control page)
            throw new Exception("UI smoke failed: expected exactly one active page content host");
        return page;
    }
    private static void CheckPageHeading(ShellWindow shell,string heading,string label)
    {
        var texts=ActivePage(shell).GetLogicalDescendants().OfType<TextBlock>()
            .Select(t=>t.Text??string.Empty).ToArray();
        Check(label+" heading: "+heading,texts.Contains(heading,StringComparer.Ordinal));
    }
    private static void Check(string label,bool passed)
    {
        TestReporter.Check("UiSmokeTests",label,passed);
    }
    private static void Pass(string description)
    {
        TestReporter.Pass("UiSmokeTests",description);
        Console.Out.Flush();
    }
}
