using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using SkiaSharp;

namespace KeyPassXCCloudSync;

internal sealed partial class ShellWindow : Window
{
    // Public repository address used by the About-page credits link.
    private const string RepositoryUrl="https://github.com/gamba69/KeePassXC-Cloud-Sync";
    private static readonly (string Label,string Version)[] LibraryVersions=
    [
        ("Avalonia",AboutDependencyVersions.Avalonia),
        ("Avalonia.Desktop",AboutDependencyVersions.AvaloniaDesktop),
        ("Avalonia.Win32",AboutDependencyVersions.AvaloniaWin32),
        ("Avalonia.Skia",AboutDependencyVersions.AvaloniaSkia),
        ("Avalonia.HarfBuzz",AboutDependencyVersions.AvaloniaHarfBuzz),
        ("Avalonia.Themes.Fluent",AboutDependencyVersions.AvaloniaThemes),
        ("FluentIcons.Avalonia",AboutDependencyVersions.FluentIcons),
        ("SkiaSharp",AboutDependencyVersions.SkiaSharp),
        ("SkiaSharp.NativeAssets.Win32",AboutDependencyVersions.SkiaAssets),
        ("SkiaSharp.HarfBuzz",AboutDependencyVersions.SkiaHarfBuzz),
        ("HarfBuzzSharp",AboutDependencyVersions.HarfBuzzSharp),
        ("HarfBuzzSharp.NativeAssets.Win32",AboutDependencyVersions.HarfBuzzAssets)
    ];
    private static readonly string HttpStackVersion=
        typeof(System.Net.Http.HttpClient).Assembly.GetName().Version?.ToString(3)??"n/a";
    private static readonly (string Label,string Version)[] CloudAccessComponents=
    [
        ("Google Drive",$"REST API v3 · System.Net.Http {HttpStackVersion}"),
        ("WebDAV",$"RFC 4918 · System.Net.Http {HttpStackVersion}"),
        ("S3 / R2",$"REST API + AWS SigV4 · System.Net.Http {HttpStackVersion}"),
        ("Proton Drive","Официальный CLI bridge · внешний инструмент")
    ];

    private Control BuildAbout()
    {
        var page=new Grid();
        var content=Column(10);
        content.Margin=new Thickness(12,10,12,5);
        var identity=new Grid{ColumnDefinitions=new ColumnDefinitions("40,*"),ColumnSpacing=10};
        _aboutBrandImage=new Image{VerticalAlignment=VerticalAlignment.Center,Stretch=Stretch.Uniform};
        BrandArtwork.Apply(_aboutBrandImage,ActualThemeVariant==Avalonia.Styling.ThemeVariant.Dark,RenderScaling,32);
        identity.Children.Add(_aboutBrandImage);
        var introduction=Column(2);
        introduction.Children.Add(Text("KeyPassXC Cloud Sync",18,true));
        // Place all About credits in the same metadata row as version,
        // platform, architecture and runtime, directly under the product name.
        // The repository stays ordinary-looking text with independent click handling.
        var credits=new WrapPanel
        {
            Name="AboutCreditsRow",Orientation=Orientation.Horizontal,
            HorizontalAlignment=HorizontalAlignment.Left,
            VerticalAlignment=VerticalAlignment.Center
        };
        var creditForeground=Secondary;
        var creditText=Text($"{BuildInfo.Version} · {DesktopPlatform.Current.PlatformName} · "+
            $"{DesktopPlatform.Current.ProcessArchitectureName} · "+
            (RuntimeFeature.IsDynamicCodeSupported?"JIT":"Native AOT")+
            " · © 2026 Igor К. Dulevich · Developed with AI assistance (OpenAI Codex) · ",
            11,false,creditForeground);
        creditText.VerticalAlignment=VerticalAlignment.Center;
        credits.Children.Add(creditText);
        if(Uri.TryCreate(RepositoryUrl,UriKind.Absolute,out var url) &&
            url.Scheme==Uri.UriSchemeHttps && url.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase))
        {
            var repositoryLink=Text(RepositoryUrl,11,false,creditForeground);
            repositoryLink.Name="AboutRepositoryLink";
            repositoryLink.VerticalAlignment=VerticalAlignment.Center;
            repositoryLink.TextWrapping=TextWrapping.NoWrap;
            repositoryLink.Cursor=new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            repositoryLink.Focusable=true;
            ToolTip.SetTip(repositoryLink,RepositoryUrl);
            repositoryLink.PointerPressed+=(_,e)=>
            {
                if(!e.GetCurrentPoint(repositoryLink).Properties.IsLeftButtonPressed)return;
                OpenRepository(url);
                e.Handled=true;
            };
            repositoryLink.KeyDown+=(_,e)=>
            {
                if(e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space))return;
                OpenRepository(url);
                e.Handled=true;
            };
            credits.Children.Add(repositoryLink);
        }
        introduction.Children.Add(credits);
        introduction.Children.Add(Muted(UiLang.T("Синхронизация зашифрованных баз KeePassXC (.kdbx) через независимые облачные подключения — без расшифровки содержимого базы."),11));
        Grid.SetColumn(introduction,1);identity.Children.Add(introduction);
        content.Children.Add(identity);
        // The operational metrics are the most useful About section.
        content.Children.Add(AboutCloudPerformance());

        // One left card spans both right-hand cards. Unlike a StackPanel in
        // the right column, this grid aligns their outer top/bottom edges.
        var details=new Grid{Name="AboutDetailsGrid",
            ColumnDefinitions=new ColumnDefinitions("1.15*,*"),
            RowDefinitions=new RowDefinitions("Auto,Auto"),
            ColumnSpacing=10,RowSpacing=10};
        var libraries=AboutLibraries();
        libraries.Name="AboutLibrariesCard";
        Grid.SetRowSpan(libraries,2);
        details.Children.Add(libraries);
        var environment=AboutEnvironment();
        environment.Name="AboutEnvironmentCard";
        Grid.SetColumn(environment,1);details.Children.Add(environment);
        var protocol=AboutProtocol();
        protocol.Name="AboutProtocolCard";
        Grid.SetColumn(protocol,1);Grid.SetRow(protocol,1);
        details.Children.Add(protocol);
        bool? previousNarrow=null;
        details.SizeChanged+=(_,e)=>
        {
            var narrow=e.NewSize.Width<760;
            if(previousNarrow==narrow)return;
            previousNarrow=narrow;
            details.ColumnDefinitions=new ColumnDefinitions(narrow?"*":"1.15*,*");
            details.RowDefinitions=new RowDefinitions(narrow?"Auto,Auto,Auto":"Auto,Auto");
            Grid.SetRowSpan(libraries,narrow?1:2);
            Grid.SetColumn(environment,narrow?0:1);
            Grid.SetRow(environment,narrow?1:0);
            Grid.SetColumn(protocol,narrow?0:1);
            Grid.SetRow(protocol,narrow?2:1);
        };
        content.Children.Add(details);
        page.Children.Add(new ScrollViewer{Content=content,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        return page;
    }
    private Control AboutLibraries()
    {
        var stack=Column(8);
        stack.Children.Add(Text(UiLang.T("Библиотеки и компоненты"),14,true));
        var columns=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*"),ColumnSpacing=14};
        for(var half=0;half<2;half++)
        {
            var items=Column(4);
            foreach(var item in LibraryVersions.Skip(half*6).Take(6)
                .Select((entry,index)=>(entry,index:index+half*6)))
                items.Children.Add(BuildStripedVersionRow(item.entry.Label,UiLang.T(item.entry.Version),item.index));
            Grid.SetColumn(items,half);columns.Children.Add(items);
        }
        stack.Children.Add(columns);
        stack.Children.Add(Text(UiLang.T("Интеграции облаков"),13,true));
        var cloudAccess=Column(4);
        foreach(var item in CloudAccessComponents.Select((entry,index)=>(entry,index)))
            cloudAccess.Children.Add(BuildStripedVersionRow(item.entry.Label,UiLang.T(item.entry.Version),item.index));
        stack.Children.Add(cloudAccess);
        stack.Children.Add(Muted(UiLang.T("Для облаков приложение использует собственные интеграции поверх стандартного сетевого стека .NET; сторонние облачные SDK не применяются."),10));
        return AboutCard(stack);
    }
    private Control AboutEnvironment()
    {
        var stack=Column(7);stack.Children.Add(Text(UiLang.T("Система и графика"),14,true));
        var rows=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*"),
            RowDefinitions=new RowDefinitions("Auto,Auto,Auto,Auto"),ColumnSpacing=10,RowSpacing=8};
        string skia;
        try{skia=SkiaSharpVersion.Native.ToString();}catch(Exception){skia=UiLang.T("Нет данных");}
        var scaling=RenderScaling*100d;
        var metrics=new (string Caption,string Data)[]
        {
            ("Платформа",DesktopPlatform.Current.PlatformName+" · "+DesktopPlatform.Current.ProcessArchitectureName),
            ("Среда",DesktopPlatform.Current.RuntimeDescription),
            ("Рендеринг","Avalonia.Skia · "+DesktopPlatform.Current.PlatformName),
            ("Нативная Skia",skia),
            ("Текст","Avalonia.HarfBuzz"),
            ("Масштаб",scaling.ToString("0.#",CultureInfo.CurrentCulture)+"%"),
            ("GPU-адаптер","не определяется через публичный API Avalonia"),
            ("Память процесса",SafeWorkingSet())
        };
        for(var i=0;i<metrics.Length;i++)
        {
            var cell=Column(2);
            cell.Children.Add(Muted(UiLang.T(metrics[i].Caption),10));
            cell.Children.Add(Text(UiLang.T(metrics[i].Data),11));
            Grid.SetColumn(cell,i%2);Grid.SetRow(cell,i/2);rows.Children.Add(cell);
        }
        stack.Children.Add(rows);
        return AboutCard(stack);
    }
    private Control AboutProtocol()
    {
        var stack=Column(5);
        stack.Children.Add(Text(UiLang.T("Синхронизация и защита"),14,true));
        stack.Children.Add(Muted(UiLang.T("Протокол V2 · WebDAV · S3 / R2 · Google Drive · Proton Drive (через официальный CLI)."),11));
        stack.Children.Add(Muted(UiLang.T("Независимые облака, история версий, резервные копии и ручное разрешение конфликтов в KeePassXC."),11));
        stack.Children.Add(Muted(UiLang.T("Учётные данные — в Windows Credential Manager; пароли из KDBX приложение не читает."),11));
        return AboutCard(stack);
    }
    private Control AboutCloudPerformance()
    {
        var stack=Column(8);
        var header=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto,Auto"),ColumnSpacing=7};
        header.Children.Add(Text(UiLang.T("Производительность облачных подключений"),14,true));
        var periods=new[]
        {
            new ComboBoxItem{Content=UiLang.T("Неделя"),Tag="week"},
            new ComboBoxItem{Content=UiLang.T("Месяц"),Tag="month"},
            new ComboBoxItem{Content=UiLang.T("Всего"),Tag="all"}
        };
        var period=new ComboBox{ItemsSource=periods,SelectedIndex=0,MinWidth=88,MinHeight=29};
        Grid.SetColumn(period,1);header.Children.Add(period);
        // The refresh handler closes over the table. Initialize it BEFORE
        // creating the delegate to satisfy definite-assignment analysis.
        var table=new ContentControl();
        var refresh=Action(UiLang.T("Обновить"),()=>RefreshPerformance(),icon:"refresh");
        refresh.MinHeight=29;refresh.Padding=new Thickness(7,3);
        Grid.SetColumn(refresh,2);header.Children.Add(refresh);
        stack.Children.Add(header);
        stack.Children.Add(table);
        var copyRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};
        var copyStatus=Muted(UiLang.T("Без имён подключений, путей к базе и учётных данных"),10);
        copyStatus.VerticalAlignment=VerticalAlignment.Center;
        copyRow.Children.Add(Action(UiLang.T("Копировать диагностику"),()=>_=CopyAboutDiagnosticsAsync(copyStatus),icon:"copy"));
        copyRow.Children.Add(copyStatus);
        stack.Children.Add(copyRow);
        stack.Children.Add(Muted(UiLang.T("Медиана и P₉₅ вычисляются по точным длительностям успешных операций (мс). Скорость — объём данных / время операций передачи, не пропускная способность сети."),10));
        stack.Children.Add(Muted(UiLang.T("Показатели на этом компьютере; накопительная статистика уже доступна на вкладке «Облака». До появления измерений отображается «Нет данных»."),10));
        void RefreshPerformance()
        {
            var selected=(period.SelectedItem as ComboBoxItem)?.Tag as string??"week";
            table.Content=BuildCloudPerformanceTable(selected);
        }
        period.SelectionChanged+=(_,_)=>RefreshPerformance();
        RefreshPerformance();
        return AboutCard(stack);
    }
    private Control BuildCloudPerformanceTable(string period)
    {
        var cfg=_app.Engine?.Current;
        if(cfg is null || cfg.Clouds.Count==0)return Muted(UiLang.T("Облачные подключения пока не настроены."),12);
        var usage=LoadUsageForUi();
        var grid=new Grid{ColumnDefinitions=new ColumnDefinitions("2*,80,80,65,78,78,75"),
            ColumnSpacing=6,RowSpacing=6};
        for(var i=0;i<=cfg.Clouds.Count;i++)grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] headers=[UiLang.T("Подключение / проверка"),UiLang.T("Операций"),UiLang.T("Медиана"),"P₉₅",UiLang.T("Ошибок"),UiLang.T("Приём"),UiLang.T("Отправка")];
        for(var col=0;col<headers.Length;col++)
        {
            var heading=Muted(headers[col],10);
            heading.TextWrapping=TextWrapping.NoWrap;
            Grid.SetColumn(heading,col);grid.Children.Add(heading);
        }
        int row=1;
        foreach(var cloud in cfg.Clouds)
        {
            AddZebraStripe(grid,row,headers.Length);
            UsageCloud? record=null;
            if(usage is not null && usage.Self.Clouds.TryGetValue(cloud.CloudId,out var recorded))
                record=recorded;
            var totals=record is null?null:UsageStatistics.Period(record,period);
            var n=totals?.Operations??0;
            ReplicaStatus? status=null;
            if(_state is not null && _state.Clouds.TryGetValue(cloud.Name,out var savedStatus))
                status=savedStatus;
            V2CloudCursor? scan=null;
            if(_state is not null && _state.V2Cursors.TryGetValue(cloud.CloudId,out var savedScan))
                scan=savedScan;
            var heading=Column(1);
            heading.Children.Add(Text(cloud.Name,11,true));
            heading.Children.Add(Muted(CloudProviderNames.Display(cloud.Provider)+" · "+
                (status?.LastCheckedUtc is { } when?when.ToLocalTime().ToString("dd.MM HH:mm"):
                UiLang.T("проверок ещё не было"))+
                (scan?.LastScanElapsedMilliseconds is { } scanMs ? " · "+FormatDuration(scanMs) : ""),10));
            ToolTip.SetTip(heading,UiLang.T("Последняя успешная проверка")+": "+
                (scan?.LastSuccessfulScanUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")??
                 UiLang.T("Нет данных")));
            Grid.SetRow(heading,row);grid.Children.Add(heading);
            var columns=new[]
            {
                n>0?n.ToString("N0"):UiLang.T("Нет данных"),
                totals?.MedianMilliseconds is { } median ?median.ToString("0.#",CultureInfo.CurrentCulture)+" ms":"—",
                totals?.P95Milliseconds is { } p95 ?p95+" ms":"—",
                n>0?(totals?.Failed??0).ToString("N0"):"—",
                FormatTransferRate(totals?.DownloadBytesPerSecond),
                FormatTransferRate(totals?.UploadBytesPerSecond)
            };
            for(var i=0;i<columns.Length;i++)
            {
                var valueText=Text(columns[i],11);
                valueText.TextWrapping=TextWrapping.NoWrap;
                valueText.TextTrimming=TextTrimming.CharacterEllipsis;
                ToolTip.SetTip(valueText,columns[i]);
                Grid.SetColumn(valueText,i+1);Grid.SetRow(valueText,row);
                grid.Children.Add(valueText);
            }
            row++;
        }
        return grid;
    }
    private static string FormatDuration(long milliseconds)
        =>milliseconds<1000?milliseconds.ToString(CultureInfo.CurrentCulture)+" ms":
            (milliseconds/1000d).ToString("0.#",CultureInfo.CurrentCulture)+" s";
    private static string FormatTransferRate(double? value)
    {
        if(value is null || !double.IsFinite(value.Value))return "—";
        if(value.Value<1024)return value.Value.ToString("0",CultureInfo.CurrentCulture)+" B/s";
        if(value.Value<1048576)return (value.Value/1024d).ToString(value.Value<10240?"0.#":"0",CultureInfo.CurrentCulture)+" KB/s";
        if(value.Value<1073741824)return (value.Value/1048576d).ToString(value.Value<10485760?"0.#":"0",CultureInfo.CurrentCulture)+" MB/s";
        return (value.Value/1073741824d).ToString("0.#",CultureInfo.CurrentCulture)+" GB/s";
    }
    private Control BuildStripedVersionRow(string label,string value,int index)
    {
        var row=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=8};
        var name=Text(label,11);
        name.TextWrapping=TextWrapping.NoWrap;
        name.TextTrimming=TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(name,label);
        row.Children.Add(name);
        var version=Muted(value,11);
        version.TextWrapping=TextWrapping.NoWrap;
        version.TextTrimming=TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(version,value);
        Grid.SetColumn(version,1);row.Children.Add(version);
        return new Border
        {
            CornerRadius=new CornerRadius(4),
            Padding=new Thickness(6,4),
            Background=index%2==1?ZebraStripeBrush():Brushes.Transparent,
            Child=row
        };
    }
    private void AddZebraStripe(Grid grid,int row,int columnCount)
    {
        if(row%2==0)return;
        var stripe=new Border
        {
            Background=ZebraStripeBrush(),
            CornerRadius=new CornerRadius(4),
            Margin=new Thickness(-4,-2,-4,-2),
            MinHeight=38
        };
        Grid.SetRow(stripe,row);
        Grid.SetColumnSpan(stripe,columnCount);
        grid.Children.Add(stripe);
    }
    private Border AboutCard(Control body)
    {
        var card=Card(body);
        card.Padding=new Thickness(11,10);
        return card;
    }
    private async Task CopyAboutDiagnosticsAsync(TextBlock status)
    {
        try
        {
            // Avalonia 12 IClipboard.SetTextAsync is an extension from Avalonia.Input.Platform.
            // The Clipboard instance is exposed via TopLevel, not directly by Window.
            var clip=TopLevel.GetTopLevel(this)?.Clipboard;
            if(clip is null)throw new InvalidOperationException(UiLang.T("Буфер обмена недоступен"));
            var lines=new StringBuilder();
            lines.AppendLine("KeyPassXC Cloud Sync "+BuildInfo.Version);
            lines.AppendLine("Platform: "+DesktopPlatform.Current.PlatformName);
            lines.AppendLine("Runtime: "+DesktopPlatform.Current.RuntimeDescription);
            lines.AppendLine("Architecture: "+DesktopPlatform.Current.ProcessArchitectureName);
            lines.AppendLine("Native Skia: "+SkiaSharpVersion.Native);
            lines.AppendLine("Working set: "+SafeWorkingSet());
            foreach(var (label,version) in LibraryVersions)
                lines.AppendLine(label+": "+version);
            foreach(var (label,version) in CloudAccessComponents)
                lines.AppendLine("Cloud access / "+label+": "+version);
            // Do not copy cloud names, IDs, local paths, credentials or KDBX content.
            var cfg=_app.Engine?.Current;
            lines.AppendLine("Configured connections: "+(cfg?.Clouds.Count??0));
            if(cfg is not null)
            {
                var usage=LoadUsageForUi();
                var index=0;
                foreach(var cloud in cfg.Clouds)
                {
                    ++index;
                    UsageCloud? item=null;
                    if(usage is not null && usage.Self.Clouds.TryGetValue(cloud.CloudId,out var found))item=found;
                    var t=item?.Total;
                    lines.AppendLine("Connection #"+index+" ("+cloud.Provider+"): operations="+
                        (t?.Operations??0)+" failed="+(t?.Failed??0)+" median_ms="+
                        (t?.MedianMilliseconds?.ToString("0.#",CultureInfo.InvariantCulture)??"n/a")+
                        " p95_ms="+(t?.P95Milliseconds?.ToString(CultureInfo.InvariantCulture)??"n/a")+
                        " download_bytes="+(t?.DownloadBytes??0)+" upload_bytes="+(t?.UploadBytes??0));
                }
            }
            await clip.SetTextAsync(lines.ToString());
            status.Text=UiLang.T("Диагностика скопирована");
        }
        catch(Exception)
        {
            status.Text=UiLang.T("Не удалось скопировать диагностику");
        }
    }
    private static string SafeWorkingSet()
    {
        return DesktopPlatform.Current.ProcessMemoryUsage;
    }
    private void OpenRepository(Uri repository)
    {
        try{PlatformServices.Current.Browser.Open(repository.AbsoluteUri);}
        catch(Exception ex){ShowProblem(UiLang.T("О программе"),UiErrorDisplay.Present(ex));}
    }
}
