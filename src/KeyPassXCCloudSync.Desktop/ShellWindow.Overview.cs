using Avalonia.Controls.Templates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;

namespace KeyPassXCCloudSync;

internal sealed partial class ShellWindow : Window
{
    private string StatusLabel()
    {
        // Status must reflect the actual configured vault, not the initialization
        // order of the UI and background worker (which starts asynchronously).
        if(string.IsNullOrWhiteSpace(_draft.LocalFile))return "Выберите базу KDBX";
        if(_draft.Clouds.Count==0)return "Добавьте облачное подключение";
        if(_state?.TopologyDisabled==true)return "Этот компьютер отключён от синхронизации";
        if(_state?.TopologyError is {Length:>0})return "Ошибка общей топологии";
        if(_app.Engine?.Current is {SyncEnabled:false})return "Синхронизация KDBX выключена";
        if(_state?.Conflict==true)return "Нужна проверка конфликтующих версий";
        if(_state is null || _state.Result=="NeverRun")
        {
            var phase=_app.Engine?.Progress.Phase;
            return phase is "CHECK" or "READY" or "SYNC"
                ? "Первоначальная проверка…" : "Ожидание первой проверки";
        }
        return _state.Result switch
        {
            "SYNCED"=>"Синхронизировано",
            "TOPOLOGY_CHANGED"=>"Получена новая топология",
            "DISABLED"=>"Этот компьютер отключён",
            "SYNCING"=>"Передача изменений…",
            "PARTIAL"=>"Сохранено не во всех облаках",
            "DEGRADED"=>"Часть облаков недоступна",
            "OFFLINE"=>"Облака недоступны",
            "CONFLICT"=>"Нужна проверка конфликтующих версий",
            "ERROR"=>"Ошибка проверки",
            _=>"Проверяю состояние подключений"
        };
    }
    private static string LocalTime(DateTimeOffset? when)
        => when?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")??"Нет данных";
    private string CloudCountdown(string cloudId)
    {
        // The countdown remains active in idle mode: only KDBX transfers are paused.
        if(_state?.TopologyDisabled==true)return "Опрос отключён";
        var activity=_app.Engine?.Activity.Get(cloudId);
        if(activity is {Active:true,StartedUtc:{ } since})
            return FormatDuration(DateTimeOffset.UtcNow-since);
        if(activity is {Active:false,Stage:"Запрошена проверка…"})return "—";
        var state=_state;
        if(state is null || !state.NextCloudPollUtc.TryGetValue(cloudId,out var next))
            return "Ожидание первой проверки";
        var seconds=Math.Max(0,(int)Math.Ceiling((next-DateTimeOffset.UtcNow).TotalSeconds));
        return seconds>=3600
            ?$"{seconds/3600:00}:{seconds/60%60:00}:{seconds%60:00}"
            :$"{seconds/60:00}:{seconds%60:00}";
    }
    private static string FormatDuration(TimeSpan value)
    {
        var seconds=Math.Max(0,(long)value.TotalSeconds);
        return seconds>=3600
            ?$"{seconds/3600:00}:{seconds/60%60:00}:{seconds%60:00}"
            :$"{seconds/60:00}:{seconds%60:00}";
    }
    private string CloudActivityLine(string cloudId,int timeoutSeconds)
    {
        var progress=_app.Engine?.Activity.Get(cloudId);
        if(progress is null)return "Ожидание проверки";
        if(!progress.Active)return progress.Stage;
        if(progress.WaitingForResponse && progress.OperationStartedUtc is { } started &&
           DateTimeOffset.UtcNow-started>TimeSpan.FromSeconds(Math.Max(30,timeoutSeconds)))
            return "Ожидание ответа сервера…";
        return progress.Stage;
    }
    private static string CloudStatusLabel(string? state)=>state switch
    {
        "SYNCED"=>"Синхронизировано",
        "TOPOLOGY_OK"=>"Топология проверена",
        "NEEDS_AUTH"=>"Требуется авторизация",
        "AUTH_ERROR"=>"Ошибка авторизации",
        "ERROR"=>"Ошибка",
        "BEHIND"=>"Проверено · согласование",
        "CONFLICT"=>"Конфликт",
        "OFFLINE"=>"Недоступно",
        "SYNCING"=>"Синхронизация…",
        _=>"Ещё не проверено"
    };
    private IBrush HealthColor(string? state) => state switch
    {
        "SYNCED" or "TOPOLOGY_OK" or "BEHIND" or "SYNCING"=>Good,
        "ERROR" or "AUTH_ERROR" or "CONFLICT"=>new SolidColorBrush(Color.Parse("#D55561")),
        "NEEDS_AUTH" or "OFFLINE"=>new SolidColorBrush(Color.Parse("#C18B32")),
        _=>Secondary
    };
    // The same three-period statistics table is used by every cloud/computer
    // card, both in Status and on the management pages. Rendering is local only.
    private Control UsageRows(Func<string,V2OperationTotals> totalsForPeriod)
    {
        // Five fully visible columns fit the narrower 350-DIP card with
        // unchanged 11-DIP typography. The upload column uses the spare space.
        var grid=new Grid{ColumnDefinitions=new ColumnDefinitions("52,57,52,66,*"),
            ColumnSpacing=4,RowSpacing=4};
        for(var i=0;i<4;i++)grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] labels=["Период","Чтение","Запись","Скачано","Загружено"];
        for(var col=0;col<labels.Length;col++)
        {
            var caption=Text(labels[col],11);
            caption.TextWrapping=TextWrapping.NoWrap;
            Grid.SetColumn(caption,col);grid.Children.Add(caption);
        }
        var periods=new[]{("Неделя","week"),("Месяц","month"),("Всего","all")};
        for(var row=0;row<periods.Length;row++)
        {
            if(row%2==0)
            {
                var stripe=new Border
                {
                    Background=ZebraStripeBrush(),
                    CornerRadius=new CornerRadius(4),
                    Margin=new Thickness(-4,-1,-4,-1),
                    MinHeight=24
                };
                Grid.SetRow(stripe,row+1);
                Grid.SetColumnSpan(stripe,labels.Length);
                grid.Children.Add(stripe);
            }
            var period=periods[row];
            var t=totalsForPeriod(period.Item2);
            var values=new[]{period.Item1,
                (t.Get+t.Probe+t.List).ToString("N0"),
                (t.Put+t.Mkdir+t.Delete).ToString("N0"),
                FormatBytes(t.DownloadBytes),FormatBytes(t.UploadBytes)};
            for(var col=0;col<values.Length;col++)
            {
                // Period names stay bright; numbers use the quieter ordinary
                // data color (instead of the white heading color).
                var value=Text(values[col],11,false,col==0?Fore:Secondary);
                value.TextWrapping=TextWrapping.NoWrap;
                value.TextTrimming=TextTrimming.CharacterEllipsis;
                ToolTip.SetTip(value,values[col]);
                Grid.SetRow(value,row+1);Grid.SetColumn(value,col);
                grid.Children.Add(value);
            }
        }
        return grid;
    }
    private static V2OperationTotals CloudTotals(UsageLocal? usage,string cloudId,string period)
    {
        var result=new V2OperationTotals();
        if(usage is null)return result;
        foreach(var snapshot in UsageStatistics.All(usage))
            if(snapshot.Clouds.TryGetValue(cloudId,out var item))
                result.Add(UsageStatistics.Period(item,period));
        return result;
    }
    private static V2OperationTotals ComputerTotals(UsageSnapshot? snapshot,
        IReadOnlySet<string> activeClouds,string period)
    {
        var result=new V2OperationTotals();
        if(snapshot is null)return result;
        foreach(var cloud in snapshot.Clouds.Values)
            if(activeClouds.Contains(cloud.CloudId))
                result.Add(UsageStatistics.Period(cloud,period));
        return result;
    }
    private UsageLocal? LoadUsageForUi()
    {
        var cfg=_app.Engine?.Current;
        if(cfg is null)return null;
        try{return UsageStatistics.LoadForDisplay(cfg,_state);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException){return null;}
    }
    private static Dictionary<string,UsageSnapshot> Snapshots(UsageLocal? usage)
        =>usage is null?new Dictionary<string,UsageSnapshot>(StringComparer.Ordinal):
            UsageStatistics.All(usage).Where(x=>x.InstallationId.Length>0)
                .GroupBy(x=>x.InstallationId,StringComparer.Ordinal)
                .ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);

    // Unified card shell: identical typography, status placement, information,
    // statistics, spacing and footer on the overview and management pages.
    private Control EntityCard(string name,string status,IBrush statusColor,
        string description,Control? details,Func<string,V2OperationTotals> usage,
        Control? actions=null,Control? titleAction=null,string? cloudId=null,
        string? idleCloudState=null)
    {
        var body=Column(8);
        var title=new Grid{ColumnDefinitions=new ColumnDefinitions(
            titleAction is null?"*,Auto":"*,Auto,Auto"),ColumnSpacing=6};
        var nameText=Text(name,17,true);
        nameText.TextWrapping=TextWrapping.NoWrap;
        nameText.TextTrimming=TextTrimming.CharacterEllipsis;
        title.Children.Add(nameText);
        var state=Text(status,12,true,statusColor);
        state.TextWrapping=TextWrapping.NoWrap;
        state.TextTrimming=TextTrimming.CharacterEllipsis;
        state.MaxWidth=166;
        ToolTip.SetTip(state,UiLang.T(status));
        if(cloudId is not null)_cloudStateLabels.Add((state,cloudId,idleCloudState??""));
        Grid.SetColumn(state,1);title.Children.Add(state);
        if(titleAction is not null)
        {
            Grid.SetColumn(titleAction,2);
            title.Children.Add(titleAction);
        }
        body.Children.Add(title);
        var descriptionLine=Muted(description,11);
        descriptionLine.TextWrapping=TextWrapping.NoWrap;
        descriptionLine.TextTrimming=TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(descriptionLine,UiLang.T(description));
        body.Children.Add(descriptionLine);
        if(details is not null)body.Children.Add(details);
        body.Children.Add(UsageRows(usage));
        // Compact only cloud/computer cards, not global status or settings.
        var card=actions is null?Card(body):CardWithFooter(body,actions,compact:true);
        card.Padding=DashboardCardPadding;
        return card;
    }
    // Highlight sign-in only when authentication is missing or explicitly
    // rejected. A network outage or generic cloud error is NOT proof that
    // credentials are missing. A successful poll is confirmation of access.
    internal static (string Label,bool Primary) LoginActionPresentation(
        string? cloudState,bool googleAccountKnown)
    {
        var requiresLogin=cloudState is "NEEDS_AUTH" or "AUTH_ERROR";
        var verified=cloudState is "SYNCED" or "TOPOLOGY_OK" or "SYNCING" or "BEHIND";
        if(verified)return ("Аккаунт",false);
        var notYetConnected=cloudState is null && !googleAccountKnown;
        return requiresLogin || notYetConnected
            ?("Войти",true):("Аккаунт",false);
    }
    private Control CloudCard(TopologyCloud cloud,UsageLocal? usage,CloudDraft? editable=null)
    {
        var status=_state is not null && _state.Clouds.TryGetValue(cloud.Name,out var found)?found:null;
        var details=Column(4);
        var checkCaption=UiLang.T(_app.Engine?.SyncEnabled==false?"Топология проверена: ":"Проверено: ");
        details.Children.Add(Muted(checkCaption+UiLang.T(LocalTime(status?.LastCheckedUtc)),11));
        var configured=_app.Engine?.Current?.Clouds.FirstOrDefault(c=>c.CloudId==cloud.Id);
        var timeout=configured?.TimeoutSeconds??60;
        var activityLine=Muted(CloudActivityLine(cloud.Id,timeout),11);
        activityLine.TextWrapping=TextWrapping.NoWrap;
        activityLine.TextTrimming=TextTrimming.CharacterEllipsis;
        _cloudActivityLabels.Add((activityLine,cloud.Id,timeout));
        details.Children.Add(activityLine);
        var pollLine=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=7};
        var pollCaption=UiLang.T(_app.Engine?.SyncEnabled==false?"Опрос топологии: каждые ":"Опрос: каждые ");
        pollLine.Children.Add(Muted(pollCaption+FormatInterval(cloud.PollSeconds),11));
        var countdown=Muted(CloudCountdown(cloud.Id),12);
        _pollCountdownLabels.Add((countdown,cloud.Id));
        Grid.SetColumn(countdown,1);pollLine.Children.Add(countdown);
        details.Children.Add(pollLine);
        if(status?.Error is {Length:>0} error)details.Children.Add(Muted(UiErrorDisplay.Present(error),11));
        if(editable is not null && status?.SuggestedAction is {Length:>0} suggestion)
            details.Children.Add(Muted(UiErrorDisplay.Present(suggestion),11));
        Control? actions=null;
        if(editable is not null)
        {
            var c=editable;
            var commands=Row(Action("Изменить",()=>_=EditCloudAsync(c)),
                Action("Удалить",()=>_=RemoveCloudAsync(c)));
            commands.Spacing=6;
            if(c.Provider is "GoogleDrive" or "ProtonDrive")
            {
                var presentation=LoginActionPresentation(status?.State,
                    c.Provider=="GoogleDrive" && c.Account.Length>0);
                var loginButton=c.Provider=="GoogleDrive"
                    ?Action(presentation.Label,()=>_=ConnectGoogleAsync(c),presentation.Primary,icon:"login")
                    :Action(presentation.Label,()=>ProtonLogin(c),presentation.Primary,icon:"login");
                ToolTip.SetTip(loginButton,UiLang.T(presentation.Primary
                    ?"Требуется вход в облако":"Повторная авторизация облака"));
                commands.Children.Add(loginButton);
            }
            else if(status?.State is "NEEDS_AUTH" or "AUTH_ERROR")
                {
                var later=Action("Позже",()=>_app.SnoozeAuth(c.Name));
                ToolTip.SetTip(later,UiLang.T("Напомнить через 24 часа"));
                commands.Children.Add(later);
            }
            // Keep all cloud action buttons on one row inside the narrower card.
            foreach(var button in commands.Children.OfType<Button>())
                button.Padding=new Thickness(8,7);
            actions=commands;
        }
        var refresh=new Button
        {
            Content=UiKit.IconGlyph("refresh",16),
            Width=29,Height=29,MinWidth=29,MinHeight=29,
            Padding=new Thickness(3),
            HorizontalContentAlignment=HorizontalAlignment.Center,
            VerticalContentAlignment=VerticalAlignment.Center,
            IsEnabled=_app.Engine?.SyncEnabled==true
        };
        ToolTip.SetTip(refresh,UiLang.T("Синхронизировать сейчас"));
        refresh.Click+=(_,_)=>
        {
            if(_app.Engine?.RequestCloudScan(cloud.Id)==true)RefreshLiveStatus();
        };
        var card=EntityCard(cloud.Name,CloudStatusLabel(status?.State),
            HealthColor(status?.State),
            CloudProviderNames.Display(cloud.Provider)+UiLang.T(" · Каталог: ")+cloud.RemoteDirectory,
            details,period=>CloudTotals(usage,cloud.Id,period),actions,refresh,cloud.Id,
            status?.State);
        return card;
    }
    private Control ComputerCard(TopologyComputer pc,UsageSnapshot? snapshot,
        IReadOnlySet<string> activeCloudIds,bool management=false)
    {
        var self=pc.Id==_state?.InstallationId;
        var status=!pc.Active?"Удалён":self?"Этот ПК":"Зарегистрирован";
        var details=Column(4);
        details.Children.Add(Muted((self?"Локальная статистика: ":"Статистика опубликована: ")+
            LocalTime(snapshot?.UpdatedUtc),11));
        if(management)details.Children.Add(Muted("ID: "+pc.Id,10));
        Control? actions=null;
        if(management && !pc.Active && !pc.Purged && !self)
        {
            actions=Row(Action("Удалить окончательно",()=>_=PurgeComputerAsync(pc)));
        }
        if(management && pc.Active)
        {
            var buttons=Row(Action("Переименовать",()=>_=RenameComputerAsync(pc)));
            if(!self)buttons.Children.Add(Action("Удалить компьютер",()=>_=RemoveComputerAsync(pc.Id,false)));
            buttons.Spacing=6;
            foreach(var button in buttons.Children.OfType<Button>())
                button.Padding=new Thickness(8,7);
            actions=buttons;
        }
        return EntityCard(pc.Name,status,pc.Active?(self?Good:Secondary):Secondary,
            "Компьютер · "+(pc.Active?"участник топологии":"архив"),details,
            period=>ComputerTotals(snapshot,activeCloudIds,period),actions);
    }
    // 350-DIP cards fit four across at 1920px / 125% Windows scaling;
    // still fit two across at the 820-DIP minimum window width.
    // WrapPanel reflows without page rebuilds on window resize.
    internal const double DashboardCardWidth=350;
    internal const double DashboardCardSpacing=10;
    // Four visible cards have three gaps BETWEEN them, not four trailing
    // margins. The banner must align with the fourth card's right border.
    internal const double OverviewStatusMaxWidth=4*DashboardCardWidth+
        3*DashboardCardSpacing;
    private static readonly Thickness DashboardCardPadding=new(13,14);
    private Control DashboardCards(List<Control> cards)
    {
        if(cards.Count==0)return Card(Muted("Нет записей."));
        var panel=new WrapPanel{Orientation=Orientation.Horizontal};
        foreach(var card in cards)
        {
            card.Width=DashboardCardWidth;
            card.Margin=new Thickness(0,0,DashboardCardSpacing,DashboardCardSpacing);
            panel.Children.Add(card);
        }
        return panel;
    }
    // Give up actual path width before moving metadata to a second row.
    // Constraining the TextBlock itself is essential: PathSegmentEllipsis does
    // nothing while its layout width remains unconstrained.
    internal const double OverviewPathMinWidth=100;
    internal const double OverviewPathPreferredWidth=260;
    internal const double OverviewTimestampReserveWidth=145;
    internal const double OverviewActionWidth=188;
    internal const double OverviewActionHeight=40;
    internal const double OverviewMetadataMaxWidth=OverviewPathPreferredWidth+
        OverviewTimestampReserveWidth*2+24;
    internal static double OverviewAvailablePathWidth(double width,double statusWidth)
    {
        const double bannerGaps=24;
        const double metadataGaps=24;
        const double actionGap=8;
        var available=width-statusWidth-OverviewActionWidth*2-actionGap-
            OverviewTimestampReserveWidth*2-bannerGaps-metadataGaps;
        return Math.Clamp(available,0,OverviewPathPreferredWidth);
    }
    internal static bool StackOverviewMetadata(double width,double statusWidth)
        =>OverviewAvailablePathWidth(width,statusWidth)<OverviewPathMinWidth;
    // The coordinator's last notification is not a current cloud operation.
    // In particular, a completed provider must never remain in the subtitle
    // merely because a different provider failed during reconciliation.
    internal static string NormalizeOverviewProgress(string phase,string lastLine,
        string? result)
    {
        if(phase is not ("CHECK" or "SYNC"))return lastLine;
        if(!lastLine.StartsWith("Проверяю изменения в ",StringComparison.Ordinal) &&
           !lastLine.StartsWith("Новая версия: ",StringComparison.Ordinal) &&
           !lastLine.StartsWith("Передана KDBX: ",StringComparison.Ordinal) &&
           !lastLine.StartsWith("Получена версия из облака",StringComparison.Ordinal))
            return lastLine;
        return result switch
        {
            "CONFLICT"=>"Требуется разрешение конфликта",
            _=>"Ожидаю следующей проверки"
        };
    }
    // Describe only current, observable work or a persisted per-cloud deadline.
    // The host WAIT timer schedules control-plane housekeeping, not remote retries.
    // The stable cloud ID resolves the deadline; the configured name is display only.
    internal static string OverviewIdleLine(AppState? state,
        IReadOnlyList<(string Name,string CloudId)> configured,DateTimeOffset now)
    {
        if(state is null || configured.Count==0)
            return UiLang.T("Ожидание первой проверки");
        if(state.Conflict || state.Result=="CONFLICT")
            return UiLang.T("Требуется разрешение конфликта");
        if(state.TopologyDisabled)return UiLang.T("Опрос отключён");

        var clouds=ConfiguredCloudStatuses.Select(configured.Select(c=>c.Name),state);
        var entries=clouds.Select((item,index)=>(item.Name,
            configured[index].CloudId,item.Status)).ToArray();

        var auth=entries.FirstOrDefault(c=>c.Status.State is "NEEDS_AUTH" or "AUTH_ERROR");
        if(auth.Status is not null)
            return UiLang.T("Требуется авторизация: ")+auth.Name;

        DateTimeOffset? NextDue(string cloudId)=>
            state.NextCloudPollUtc.TryGetValue(cloudId,out var due)?due:null;
        string Deadline(DateTimeOffset? due)
        {
            if(due is null)return UiLang.T(" · время следующей попытки неизвестно");
            if(due<=now)return UiLang.T(" · ожидает запуска");
            var seconds=Math.Max(1,(long)Math.Ceiling((due.Value-now).TotalSeconds));
            return UiLang.T(" · через ")+FormatDuration(TimeSpan.FromSeconds(seconds));
        }
        var failures=entries.Where(c=>c.Status.State is "ERROR" or "OFFLINE")
            .OrderBy(c=>NextDue(c.CloudId)??DateTimeOffset.MaxValue).ToArray();
        if(failures.Length>0)
        {
            var failed=failures[0];
            return UiLang.T("Повторная попытка: ")+failed.Name+
                Deadline(NextDue(failed.CloudId));
        }
        var pending=entries.FirstOrDefault(c=>c.Status.State is "BEHIND" or "SYNCING");
        if(pending.Status is not null)
            return UiLang.T("Ожидание согласования: ")+pending.Name;

        var next=entries.Select(c=>(c.Name,Due:NextDue(c.CloudId)))
            .Where(c=>c.Due is not null)
            .OrderBy(c=>c.Due).FirstOrDefault();
        if(next.Due is null)
            return UiLang.T("Ожидание расписания проверок");
        var label=state.Result is "PARTIAL" or "ERROR"
            ?"Ожидание подтверждения версий · следующая проверка: "
            :"Следующая проверка: ";
        return UiLang.T(label)+next.Name+Deadline(next.Due);
    }

    private string OverviewProgressLabel()
    {
        var engine=_app.Engine;
        if(engine is null)return UiDiagnostics.Progress("Ожидаю запуска проверок");
        var current=engine.Progress;
        var line=engine.ProgressLine();
        // Derive cloud activity from the live per-cloud tracker, not from an
        // old coordinator callback. Multiple providers may run concurrently.
        if(engine.SyncEnabled && current.Phase is not ("ERROR" or "PAUSED" or "DEBOUNCE"))
        {
            var active=engine.Current?.Clouds
                .Select(cloud=>(cloud.Name,Snapshot:engine.Activity.Get(cloud.CloudId)))
                .Where(cloud=>cloud.Snapshot?.Active==true).ToArray();
            if(active is {Length:1})
                return UiLang.T(active[0].Snapshot!.Stage)+" · "+active[0].Name;
            if(active is {Length:>1})
                return UiLang.T("Проверяются облака: ")+active.Length;
        }
        // A host WAIT deadline is not the next provider check. Use the
        // actual persisted cloud deadline and the last per-cloud outcome.
        if(engine.SyncEnabled && current.Phase=="WAIT")
        {
            var configured=engine.Current?.Clouds
                .Select(c=>(c.Name,c.CloudId)).ToArray() ?? [];
            return OverviewIdleLine(_state,configured,DateTimeOffset.UtcNow);
        }
        var normalized=NormalizeOverviewProgress(current.Phase,line,_state?.Result);
        if(engine.SyncEnabled && !string.Equals(normalized,line,StringComparison.Ordinal))
        {
            var configured=engine.Current?.Clouds
                .Select(c=>(c.Name,c.CloudId)).ToArray() ?? [];
            return OverviewIdleLine(_state,configured,DateTimeOffset.UtcNow);
        }
        var progress=UiDiagnostics.Progress(normalized);
        return !engine.SyncEnabled
            ?progress+" · "+UiLang.T("KDBX на паузе · топология и статистика работают")
            :progress;
    }
    private Control BuildOverview()
    {
        var root=Column(14);
        root.Children.Add(Text("Состояние",27,true));
        // Keep the active conflict action next to the status heading.
        if(_state?.Conflict==true)
            root.Children.Add(BuildActiveConflictPanel(SavedDataDirectory()));

        // Use available width for the actual status, not a narrow fixed column.
        // The first row remains status + actions; the secondary metadata row is
        // only needed when the window cannot fit all three groups horizontally.
        var banner=new Grid
        {
            Name="OverviewStatusRow",
            ColumnDefinitions=new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions=new RowDefinitions("Auto,Auto"),
            ColumnSpacing=12,RowSpacing=10,
            VerticalAlignment=VerticalAlignment.Center
        };
        var status=Column(3);
        status.Name="OverviewStatusSummary";
        status.MaxWidth=390;
        status.VerticalAlignment=VerticalAlignment.Center;
        _statusHeadline=Text(StatusLabel(),16,true);
        _statusHeadline.Name="OverviewStatusHeadline";
        _statusHeadline.TextWrapping=TextWrapping.Wrap;
        _statusHeadline.TextTrimming=TextTrimming.None;
        ToolTip.SetTip(_statusHeadline,UiLang.T(StatusLabel()));
        status.Children.Add(_statusHeadline);
        _liveStatus=Muted(OverviewProgressLabel(),11);
        _liveStatus.Name="OverviewStatusProgress";
        _liveStatus.TextWrapping=TextWrapping.Wrap;
        _liveStatus.TextTrimming=TextTrimming.None;
        ToolTip.SetTip(_liveStatus,UiLang.T(OverviewProgressLabel()));
        status.Children.Add(_liveStatus);
        Grid.SetColumn(status,0);banner.Children.Add(status);

        // Compact label/value groups share a common baseline. Only the vault
        // path may be shortened; its complete value stays in the tooltip.
        var details=new Grid
        {
            Name="OverviewStatusInfo",
            ColumnDefinitions=new ColumnDefinitions("Auto,Auto,Auto"),
            ColumnSpacing=12,
            MaxWidth=OverviewMetadataMaxWidth,
            HorizontalAlignment=HorizontalAlignment.Left,
            VerticalAlignment=VerticalAlignment.Center
        };
        // Explicitly constrain the vault group and its TextBlock. Auto columns
        // keep the three metadata groups tightly packed instead of leaving a
        // stretchable gap between the path and the timestamps.
        StackPanel MetadataGroup(string title,TextBlock value,string captionName)
        {
            var group=Column(3);
            group.VerticalAlignment=VerticalAlignment.Center;
            var caption=Muted(title,11);
            caption.Name=captionName;
            caption.TextWrapping=TextWrapping.NoWrap;
            group.Children.Add(caption);
            group.Children.Add(value);
            return group;
        }
        TextBlock MetadataValue(string text,string name,string tooltip,bool canTrim=false)
        {
            var value=Text(text,12);
            value.Name=name;
            value.TextWrapping=TextWrapping.NoWrap;
            value.TextTrimming=canTrim?TextTrimming.PathSegmentEllipsis:TextTrimming.None;
            ToolTip.SetTip(value,tooltip);
            return value;
        }
        var database=_draft.LocalFile.Length>0
            ?_draft.LocalFile:UiLang.T("ещё не выбрана");
        var databaseLabel=MetadataValue(database,"OverviewVaultPath",database,canTrim:true);
        databaseLabel.Width=OverviewPathPreferredWidth;
        var vault=MetadataGroup(UiLang.T("База: ").TrimEnd().TrimEnd(':'),
            databaseLabel,"OverviewVaultCaption");
        vault.Width=OverviewPathPreferredWidth;
        details.Children.Add(vault);

        var checkText=LocalTime(_state?.LastSuccessUtc);
        var checkLabel=MetadataValue(checkText,"OverviewLastCheck",checkText);
        var check=MetadataGroup(UiLang.T("Проверка: ").TrimEnd().TrimEnd(':'),
            checkLabel,"OverviewCheckCaption");
        Grid.SetColumn(check,1);details.Children.Add(check);

        var changesText=LocalTime(_state?.LastChangeUtc);
        var changesLabel=MetadataValue(changesText,"OverviewLastChange",changesText);
        var change=MetadataGroup(UiLang.T("Изменения: ").TrimEnd().TrimEnd(':'),
            changesLabel,"OverviewChangeCaption");
        Grid.SetColumn(change,2);details.Children.Add(change);
        Grid.SetColumn(details,1);banner.Children.Add(details);

        var active=_app.Engine?.SyncEnabled==true;
        var controls=Row();
        controls.Name="OverviewStatusActions";
        controls.Spacing=8;
        controls.VerticalAlignment=VerticalAlignment.Center;
        controls.HorizontalAlignment=HorizontalAlignment.Right;
        var toggleCaption=active?"Остановить":"Включить";
        var toggleIcon=active?"dismiss":"play";
        var toggle=Action(toggleCaption,()=>_=ToggleSyncAsync(),icon:toggleIcon);
        toggle.Name="OverviewToggleSync";
        toggle.IsEnabled=!_switchingSync && _app.Engine?.Current is not null;
        toggle.Width=OverviewActionWidth;toggle.Height=OverviewActionHeight;
        ToolTip.SetTip(toggle,UiLang.T(active
            ?"Выключить синхронизацию":"Включить синхронизацию"));
        if(active)
        {
            var sync=Action("Синхронизировать",()=>_app.Engine?.Wake(),icon:"refresh");
            sync.Name="OverviewSyncNow";
            sync.IsEnabled=!_switchingSync;
            sync.Width=OverviewActionWidth;sync.Height=OverviewActionHeight;
            ToolTip.SetTip(sync,UiLang.T("Синхронизировать сейчас"));
            controls.Children.Add(sync);
        }
        controls.Children.Add(toggle);
        Grid.SetColumn(controls,2);banner.Children.Add(controls);

        var stacked=true;
        void SetMetadataLayout()
        {
            var statusWidth=status.Bounds.Width>1?status.Bounds.Width:status.DesiredSize.Width;
            if(statusWidth<=1)statusWidth=status.MaxWidth;
            var availablePath=OverviewAvailablePathWidth(banner.Bounds.Width,statusWidth);
            var shouldStack=availablePath<OverviewPathMinWidth;
            // Resize the actual text bounds BEFORE deciding whether to stack.
            // In stacked layout the path can use its preferred width again.
            var pathWidth=shouldStack?OverviewPathPreferredWidth:availablePath;
            if(Math.Abs(databaseLabel.Width-pathWidth)>0.5)
            {
                databaseLabel.Width=pathWidth;
                vault.Width=pathWidth;
            }
            if(stacked==shouldStack)return;
            stacked=shouldStack;
            Grid.SetRow(details,stacked?1:0);
            Grid.SetColumn(details,stacked?0:1);
            Grid.SetColumnSpan(details,stacked?3:1);
            banner.RowSpacing=stacked?10:0;
        }
        // Initially stack until the measured width is available. Reevaluate on
        // resize or when a translated/live status changes its measured width.
        Grid.SetRow(details,1);
        Grid.SetColumn(details,0);
        Grid.SetColumnSpan(details,3);
        banner.SizeChanged+=(_,args)=>SetMetadataLayout();
        status.SizeChanged+=(_,args)=>SetMetadataLayout();
        var statusCard=Card(banner);
        statusCard.Name="OverviewStatusCard";
        statusCard.Padding=new Thickness(17,12);
        statusCard.MaxWidth=OverviewStatusMaxWidth;
        statusCard.HorizontalAlignment=HorizontalAlignment.Left;
        // Keep the panel fluid below four card slots and left-aligned above
        // them. Cloud count must not affect its width; the window may be wider.
        root.SizeChanged+=(_,e)=>
        {
            if(e.NewSize.Width<=0)return;
            var width=Math.Min(OverviewStatusMaxWidth,e.NewSize.Width);
            if(double.IsNaN(statusCard.Width) || Math.Abs(statusCard.Width-width)>0.5)
                statusCard.Width=width;
        };
        root.Children.Add(statusCard);

        var cfg=_app.Engine?.Current;
        var usage=LoadUsageForUi();
        TopologyDocument? topology=null;
        if(cfg is not null)
        {
            try{topology=TopologyCoordinator.Load(cfg);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException)
            {root.Children.Add(Card(Muted("Топология недоступна: "+UiErrorDisplay.Present(ex))));}
        }
        var snapshots=Snapshots(usage);
        root.Children.Add(Text("Облака",20,true));
        var cloudList=topology?.Clouds.Where(c=>_draft.Clouds.Any(d=>d.CloudId==c.Id)).ToList()
            ??_draft.Clouds.Select(c=>new TopologyCloud{Id=c.CloudId,Name=c.Name,Provider=c.Provider,
                RemoteDirectory=c.Remote,PollSeconds=c.Poll}).ToList();
        var cloudCards=cloudList.Select(c=>CloudCard(c,usage)).ToList();
        if(cloudCards.Count==0)cloudCards.Add(Card(Muted("Облака пока не настроены.")));
        root.Children.Add(DashboardCards(cloudCards));

        root.Children.Add(Text("Компьютеры",20,true));
        var computers=topology?.Computers.Where(c=>c.Active).ToList()
            ??[new TopologyComputer{Id=_state?.InstallationId??"",Name=PlatformServices.Current.Paths.MachineName}];
        var activeCloudIds=cloudList.Select(c=>c.Id).ToHashSet(StringComparer.Ordinal);
        var pcCards=new List<Control>();
        foreach(var pc in computers)
        {
            snapshots.TryGetValue(pc.Id,out var snapshot);
            pcCards.Add(ComputerCard(pc,snapshot,activeCloudIds));
        }
        root.Children.Add(DashboardCards(pcCards));
        root.Children.Add(Muted("Операции — логические вызовы транспорта. Статистика других компьютеров обновляется при их публикации; статус «Зарегистрирован» не означает, что ПК сейчас в сети.",11));
        if(_state?.TopologyError is {Length:>0} problem)
            root.Children.Add(Section("Проблема топологии",Text(UiErrorDisplay.Present(problem),13,true)));
        return Wrap(root);
    }
    private static string FormatInterval(int seconds)
        =>seconds%3600==0 && seconds>=3600?$"{seconds/3600} {UiLang.T("ч")}":
          seconds%60==0 && seconds>=60?$"{seconds/60} {UiLang.T("мин")}":$"{seconds} {UiLang.T("с")}";
    private static string FormatBytes(long n)
    {
        // Decimal traffic units: 1 KB = 1000 B, 1 MB = 1000 KB, etc.
        // Only formatting changes; stored byte counters remain exact.
        if(n<1_000L)return n.ToString("N0")+" B";
        if(n<1_000_000L)return (n/1_000.0).ToString("N1")+" KB";
        if(n<1_000_000_000L)return (n/1_000_000.0).ToString("N1")+" MB";
        if(n<1_000_000_000_000L)return (n/1_000_000_000.0).ToString("N2")+" GB";
        return (n/1_000_000_000_000.0).ToString("N2")+" TB";
    }
    private Control ArchivePageHeader(string title,bool selected,Action<bool> change)
    {
        // One full-width header row; the checkbox is flush right, not a
        // separate vertical block taking space above the cards.
        var header=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=15};
        header.Children.Add(Text(title,27,true));
        var checkbox=new CheckBox{Content=UiLang.T("Показывать удалённые"),IsChecked=selected,
            VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right};
        checkbox.IsCheckedChanged+=(_,_)=>change(checkbox.IsChecked==true);
        Grid.SetColumn(checkbox,1);
        header.Children.Add(checkbox);
        return header;
    }
    private Control PendingPurgeNotice()
    {
        return Card(Muted(UiLang.T("Удаление ожидает публикации общей топологии. Карточка временно скрыта; статистика будет очищена только после успешной публикации. Если публикация не удастся, запрос останется ожидающим."),12));
    }
    private Control BuildComputers()
    {
        var root=Column(17);
        root.Children.Add(ArchivePageHeader("Компьютеры",_showArchivedComputers,value=>
        {
            if(value==_showArchivedComputers)return;
            _showArchivedComputers=value;SaveUsageUi();RefreshCurrentPage();
        }));
        root.Children.Add(Muted("Переименование и управление участниками общей топологии. Удаление ПК не отзывает доступ на сервере облака."));
        var cfg=_app.Engine?.Current;
        var pending=cfg is null?null:TopologyCoordinator.ReadPendingPurge(cfg);
        if(pending?.ComputerId is not null)root.Children.Add(PendingPurgeNotice());
        var pcs=cfg is null?new List<TopologyComputer>():
            TopologyCoordinator.Load(cfg)?.Computers??new List<TopologyComputer>();
        var usage=LoadUsageForUi();
        var snapshots=Snapshots(usage);
        var activeCloudIds=_draft.Clouds.Select(c=>c.CloudId).ToHashSet(StringComparer.Ordinal);
        if(_showArchivedComputers && usage is not null)
            foreach(var old in UsageStatistics.All(usage).SelectMany(pc=>pc.Clouds.Keys))
                activeCloudIds.Add(old);
        var cards=new List<Control>();
        foreach(var pc in pcs.Where(pc=>!pc.Purged && pc.Id!=pending?.ComputerId &&
            (pc.Active||_showArchivedComputers)))
        {
            snapshots.TryGetValue(pc.Id,out var snapshot);
            cards.Add(ComputerCard(pc,snapshot,activeCloudIds,true));
        }
        root.Children.Add(DashboardCards(cards));
        if(_state?.TopologyDisabled==true)
            root.Children.Add(Card(Action("Присоединиться заново",()=>_=RejoinAsync(),true)));
        else if(pcs.Any(c=>c.Id==_state?.InstallationId && c.Active))
        {
            var danger=Action("Покинуть синхронизацию",()=>_=RemoveComputerAsync(_state!.InstallationId,true),true);
            danger.Background=new SolidColorBrush(Color.Parse("#BB3548"));
            danger.Foreground=Brushes.White;
            root.Children.Add(Section("Опасная операция",danger,
                "При выходе этот компьютер прекращает синхронизацию и очищает собственные секреты KeyPassXC Cloud Sync. KDBX остаётся на месте."));
        }
        return Wrap(root);
    }
    private async Task RenameComputerAsync(TopologyComputer computer)
    {
        var cfg=_app.Engine?.Current;
        if(cfg is null)return;
        var dialog=new Window
        {
            Title=UiLang.T("Переименовать компьютер"),Width=480,SizeToContent=SizeToContent.Height,
            CanResize=false,CanMinimize=false,ShowInTaskbar=false,
            WindowDecorations=WindowDecorations.Full,Background=UiKit.DialogBackground,
            WindowStartupLocation=WindowStartupLocation.CenterOwner
        };
        AppWindowIcons.Attach(dialog);
        var layout=new DockPanel{LastChildFill=true};
        var input=UiKit.Input(computer.Name);
        var buttons=Row(
            Action("Сохранить",()=>dialog.Close(input.Text?.Trim()),true),
            Action("Отмена",()=>dialog.Close((string?)null)));
        buttons.HorizontalAlignment=HorizontalAlignment.Right;
        var footer=new Border{Margin=new Thickness(12,0,12,12),Padding=new Thickness(10,8),
            Background=new SolidColorBrush(Color.Parse(Dark?"#102133":"#EEF2F6")),Child=buttons};
        DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
        var body=Column(12);
        body.Children.Add(Text("Имя компьютера",15,true));body.Children.Add(input);
        var card=UiKit.Card(body,Dark);card.Padding=new Thickness(14,12);
        card.Margin=new Thickness(12);
        layout.Children.Add(new ScrollViewer{Content=card,MaxHeight=400});
        dialog.Content=layout;
        dialog.KeyDown+=(_,e)=>{if(e.Key==Avalonia.Input.Key.Escape)
            {e.Handled=true;dialog.Close((string?)null);}};
        var name=await dialog.ShowDialog<string?>(this);
        if(name is null)return;
        if(name.Length<1 || name.Length>80 || name.Any(char.IsControl))
        {
            await MessageDialog.Info(this,"Недопустимое имя","Имя должно содержать от 1 до 80 символов без управляющих знаков.");
            return;
        }
        if(name==computer.Name)return;
        try
        {
            await _app.Engine!.Gate.WaitAsync();
            try{TopologyCoordinator.QueueRenameComputer(cfg,computer.Id,name);}
            finally{_app.Engine.Gate.Release();}
            _app.Engine.Wake();
            await MessageDialog.Info(this,"Переименование","Изменение имени будет распространено с топологией.");
        }
        catch(Exception e){ShowProblem("Переименование",e.Message);}
    }
    private async Task RemoveComputerAsync(string id,bool self)
    {
        var cfg=_app.Engine?.Current;
        if(cfg is null)return;
        if(!await MessageDialog.Ask(this,self?"Покинуть синхронизацию":"Удалить компьютер",
            self?"После публикации удаления программа отключит этот ПК и очистит свои локальные секреты. KDBX сохранится.":
            "Удалённый ПК отключится и очистит секреты при получении обновлённой топологии. Это не отзыв серверного доступа.",danger:true))return;
        try
        {
            await _app.Engine!.Gate.WaitAsync();
            try{TopologyCoordinator.QueueRemoveComputer(cfg,id,self);}
            finally{_app.Engine.Gate.Release();}
            _app.Engine.Wake();
            await MessageDialog.Info(this,"Запрос сохранён","Изменение топологии будет опубликовано при ближайшем соединении с облаками.");
        }
        catch(Exception e){ShowProblem("Изменение топологии",e.Message);}
    }
    private async Task PurgeComputerAsync(TopologyComputer pc)
    {
        var message=string.Format(UiLang.T("Компьютер «{0}» будет убран из архива и статистики после публикации топологии."),pc.Name)+
            "\n\n"+UiLang.T("Имя и статистика исчезнут из приложения. Минимальный идентификатор останется для защиты от повторного подключения.")+
            "\n\n"+UiLang.T("Облачные файлы и резервные копии физически не удаляются. Продолжить?");
        if(!await MessageDialog.AskAction(this,"Окончательно удалить компьютер",message,
            "Удалить окончательно",danger:true))return;
        await QueuePurgeAsync(cfg=>TopologyCoordinator.QueuePurgeComputer(cfg,pc.Id));
    }
    private async Task PurgeCloudAsync(string id,string name)
    {
        var message=string.Format(UiLang.T("Облако «{0}» будет убрано из архива и статистики после публикации топологии."),name)+
            "\n\n"+UiLang.T("Статистика исчезнет из приложения без автоматического восстановления.")+
            "\n\n"+UiLang.T("Облачные файлы и резервные копии физически не удаляются. Продолжить?");
        if(!await MessageDialog.AskAction(this,"Окончательно удалить облако",message,
            "Удалить окончательно",danger:true))return;
        await QueuePurgeAsync(cfg=>TopologyCoordinator.QueuePurgeCloud(cfg,id));
    }
    private async Task QueuePurgeAsync(Action<AppOptions> queue)
    {
        var cfg=_app.Engine?.Current;
        if(cfg is null)return;
        try
        {
            await _app.Engine!.Gate.WaitAsync();
            try{queue(cfg);}
            finally{_app.Engine.Gate.Release();}
            // The cached topology still contains the archived record until it is
            // published. Rebuild this page immediately using the local pending
            // purge hint; do not remove usage or touch the topology cache here.
            RefreshCurrentPage();
            _app.Engine.Wake();
            await MessageDialog.Info(this,"Очистка запланирована",
                UiLang.T("Карточка скрыта из архива на этом компьютере. Окончательное удаление и очистка статистики произойдут только после публикации общей топологии в облаке."));
        }
        catch(Exception ex){ShowProblem("Окончательное удаление",ex.Message);}
    }
    private async Task RejoinAsync()
    {
        var cfg=_app.Engine?.Current;
        if(cfg is null)return;
        if(!await MessageDialog.Ask(this,"Присоединиться заново",
            "Будет создан новый идентификатор компьютера. Сначала убедитесь, что облачные реквизиты настроены."))return;
        await _app.Engine!.Gate.WaitAsync();
        try
        {
            var state=SyncStorage.LoadState(cfg.StateFile);
            state.InstallationId=Guid.NewGuid().ToString("N");
            state.TopologyDisabled=false;state.RemoteTopologyRevision=0;state.TopologyRefreshNeeded=true;
            SyncStorage.SaveState(cfg.StateFile,state);
            File.Delete(TopologyCoordinator.CachePath(cfg));
        }
        finally{_app.Engine.Gate.Release();}
        _app.Engine.Wake();RefreshCurrentPage();
    }

    private Control BuildClouds()
    {
        var root=Column(17);
        root.Children.Add(ArchivePageHeader("Облака",_showArchivedClouds,value=>
        {
            if(value==_showArchivedClouds)return;
            _showArchivedClouds=value;SaveUsageUi();RefreshCurrentPage();
        }));
        root.Children.Add(Muted("Поддерживаются WebDAV Storage, S3 Storage, Google Drive и Proton Drive. Главного облака нет."));

        var usage=LoadUsageForUi();
        var cfg=_app.Engine?.Current;
        var pending=cfg is null?null:TopologyCoordinator.ReadPendingPurge(cfg);
        if(pending?.CloudId is not null)root.Children.Add(PendingPurgeNotice());
        var cloudCards=new List<Control>();
        foreach(var c in _draft.Clouds)
        {
            var cloud=new TopologyCloud{Id=c.CloudId,Name=c.Name,Provider=c.Provider,
                RemoteDirectory=c.Remote,PollSeconds=c.Poll};
            cloudCards.Add(CloudCard(cloud,usage,c));
        }
        if(_showArchivedClouds)
        {
            // Deleted clouds are permanently absent from active transports.
            // Keep lightweight names in topology for clients that never saw
            // the old connection, plus usage-only older entries as fallback.
            var currentIds=_draft.Clouds.Select(c=>c.CloudId).ToHashSet(StringComparer.Ordinal);
            var archived=new Dictionary<string,(string Name,string Provider)>(StringComparer.Ordinal);
            if(cfg is not null)
            {
                try
                {
                    foreach(var c in TopologyCoordinator.Load(cfg)?.RemovedClouds ?? new List<TopologyRemovedCloud>())
                        if(!currentIds.Contains(c.Id))archived[c.Id]=(c.Name,c.Provider);
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
                    System.Text.Json.JsonException or SyncSafetyException){}
            }
            if(usage is not null)
                foreach(var item in UsageStatistics.All(usage).SelectMany(snapshot=>snapshot.Clouds.Values))
                    if(!currentIds.Contains(item.CloudId))
                        archived.TryAdd(item.CloudId,(item.Name,""));
            var purgedIds=cfg is null?new HashSet<string>(StringComparer.Ordinal):
                (TopologyCoordinator.Load(cfg)?.PurgedCloudIds??[]).ToHashSet(StringComparer.Ordinal);
            foreach(var removed in purgedIds)archived.Remove(removed);
            foreach(var item in archived.OrderBy(x=>x.Value.Name,StringComparer.OrdinalIgnoreCase))
            {
                var id=item.Key;
                if(id==pending?.CloudId)continue;
                var details=Muted("Подключение удалено · сетевые запросы отключены",11);
                // An archive-only usage snapshot with no shared tombstone cannot
                // be purged safely because the cloud's identity is not registered.
                var inTopology=cfg is not null && Guid.TryParseExact(id,"N",out _) &&
                    (TopologyCoordinator.Load(cfg)?.RemovedClouds.Any(c=>c.Id==id)==true ||
                     (usage is not null && UsageStatistics.All(usage).Any(s=>s.Clouds.ContainsKey(id))));
                var actions=inTopology?Row(Action("Удалить окончательно",()=>_=PurgeCloudAsync(id,item.Value.Name))):null;
                cloudCards.Add(EntityCard(item.Value.Name,"Удалено",Secondary,"Архив облака",details,
                    period=>CloudTotals(usage,id,period),actions));
            }
        }
        if(cloudCards.Count==0)cloudCards.Add(Card(Muted("Облачных подключений пока нет.")));
        root.Children.Add(DashboardCards(cloudCards));

        // Commands belong to the page, not to oversized cards. Keep one
        // left-aligned toolbar in both the single- and two-column layouts.
        var add=Row(
            Action("WebDAV Storage",()=>_=EditCloudAsync(null,"WebDAV"),icon:"plus"),
            Action("S3 Storage",()=>_=EditCloudAsync(null,"S3"),icon:"plus"),
            Action("Google Drive",()=>_=EditCloudAsync(null,"GoogleDrive"),icon:"plus"),
            Action("Proton Drive",()=>_=EditCloudAsync(null,"ProtonDrive"),icon:"plus"));
        add.HorizontalAlignment=HorizontalAlignment.Left;
        root.Children.Add(add);

        if(_draft.Clouds.Any(c=>c.Provider=="GoogleDrive" && c.ClientId.Length==0) &&
           GoogleOAuthClientDefaults.ClientId.Length==0)
            root.Children.Add(Section("Подключение Google OAuth",Muted("В этой сборке не задан общий OAuth Client ID издателя. Для входа укажите собственный Desktop OAuth Client ID в настройках подключения. Пользовательские токены не включаются в программу.")));
        return Wrap(root);
    }

    // Persist cloud changes independently of uncommitted fields on Settings.
    private async Task<bool> SaveCloudDraftAsync()
    {
        var pending=_draft;
        try
        {
            var onlyClouds=File.Exists(_configPath)?SettingsDraft.Load(_configPath):pending.Clone();
            onlyClouds.Clouds=pending.Clouds.Select(c=>c.Clone()).ToList();
            _draft=onlyClouds;
            var saved=await SaveAsync(quiet:true);
            if(saved)pending.Clouds=_draft.Clouds.Select(c=>c.Clone()).ToList();
            return saved;
        }
        finally{_draft=pending;RefreshCurrentPage();}
    }
    private async Task EditCloudAsync(CloudDraft? existing,string type="GoogleDrive")
    {
        var dialog=new CloudDialog(existing?.Provider??type,existing,_draft.Poll,_configPath);
        var edited=await dialog.ShowDialog<CloudDraft?>(this);
        if(edited is null)return;
        var before=_draft.Clone();
        if(_draft.Clouds.Any(c=>c!=existing && c.Name.Equals(edited.Name,StringComparison.OrdinalIgnoreCase)))
        {await MessageDialog.Info(this,"Повторяющееся имя","Такое имя облака уже используется.");return;}
        if(existing is null)_draft.Clouds.Add(edited);
        else
        {
            if(existing.Provider!=edited.Provider || existing.Remote!=edited.Remote ||
               existing.BaseUrl!=edited.BaseUrl || existing.Root!=edited.Root)
                edited.CloudId=Guid.NewGuid().ToString("N");
            var i=_draft.Clouds.IndexOf(existing);if(i>=0)_draft.Clouds[i]=edited;
        }
        if(!await SaveCloudDraftAsync())_draft=before;
        RefreshCurrentPage();
    }
    private async Task RemoveCloudAsync(CloudDraft cloud)
    {
        if(!await MessageDialog.Ask(this,"Отключить облако?",
            UiLang.T("Отключение «")+cloud.Name+UiLang.T("» удалит подключение из настроек, но не удалит файлы в облаке.")))return;
        if(_draft.Clouds.Count==1)
        {
            await MessageDialog.Info(this,"Последнее облако",
                "Для сохранения действующей конфигурации необходимо хотя бы одно облако. Сначала добавьте другое подключение.");
            return;
        }
        var before=_draft.Clone();
        _draft.Clouds.Remove(cloud);
        if(!await SaveCloudDraftAsync())_draft=before;
        RefreshCurrentPage();
    }
    private async Task ConnectGoogleAsync(CloudDraft cloud)
    {
        if(_authenticating)return;
        AppOptions cfg;
        try
        {
            cfg=SettingsStore.Preview(_draft,_configPath);
            var option=cfg.Clouds.Single(c=>c.Name==cloud.Name);
            if(option.ClientId.Length==0)
            {
                await MessageDialog.Info(this,"OAuth-клиент не настроен",
                    "В этой сборке отсутствует общий Google OAuth Client ID. Откройте настройки облака и укажите свой Desktop Client ID и Client Secret; в публичной сборке эти данные будут встроены разработчиком.");
                return;
            }
            if(cloud.GoogleSecret.Length>0)
                PlatformServices.Current.Secrets.Write(cloud.SecretTarget,cloud.ClientId,cloud.GoogleSecret);
            _authenticating=true;
            _lastResult="Google OAuth: откройте браузер и разрешите доступ";
            var engine=_app.Engine!;
            await engine.Gate.WaitAsync();
            string address;
            try
            {
                using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(option.TimeoutSeconds)};
                address=await new GoogleOAuth(cfg.AsTransportConfig(option),http).LoginAsync(CancellationToken.None);
            }
            finally{engine.Gate.Release();}
            cloud.Account=address;
            _lastResult="Google подключён: "+address;
            if(await SaveCloudDraftAsync())
                await MessageDialog.Info(this,"Google Drive подключён",
                    "Аккаунт: "+address+"\nАвторизация сохранена.");
        }
        catch(Exception e){await MessageDialog.Info(this,"Ошибка авторизации Google",UiErrorDisplay.Present(e));}
        finally{_authenticating=false;RefreshCurrentPage();}
    }
    private void ProtonLogin(CloudDraft c)
    {
        try
        {
            var executable=PlatformServices.Current.Paths.ResolveProtonExecutable(c.Executable,_configPath);
            if(executable.Length==0)throw new FileNotFoundException(
                "Не найден proton-drive.exe рядом с программой или в PATH. Укажите путь к CLI в настройках Proton.");
            if(!File.Exists(executable))throw new FileNotFoundException("Не найден Proton Drive CLI",executable);
            PlatformServices.Current.Processes.StartProtonLogin(executable);
        }
        catch(Exception e){ShowProblem("Не удалось запустить Proton CLI",e.Message);}
    }
    // The initial check and the post-confirmation check are READ-ONLY. Only the
    // second, unchanged result may authorize the local transition to enabled.
}
