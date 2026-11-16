using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace KeyPassXCCloudSync;

// This Avalonia window is used by the main application, the temporary AOT
// update helper and the one-shot post-restart acknowledgement. The helper never
// creates a synchronization engine or reads the KDBX database.
internal sealed class UpdateOperationWindow : Window
{
    private readonly TextBlock _title;
    private readonly TextBlock _versions;
    private readonly TextBlock _phase;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private readonly Button _button;
    private readonly Image _brand;
    private readonly CancellationTokenSource _cancellation=new();
    private readonly string _language;
    private bool _working;
    private bool _canCancel;
    private bool _finished;
    private bool _helperMode;
    private bool _reactivatedAfterOwnerExit;
    private int _foregroundRequest;

    internal UpdateOperationWindow(string operation,string fromVersion,string toVersion,
        string? language=null)
    {
        _language=language is "ru" or "uk" or "en"?language:UiLang.Active;
        Width=470;MinWidth=440;SizeToContent=SizeToContent.Height;
        CanResize=false;
        ShowInTaskbar=true;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Icon=AppWindowIcons.White;
        var heading=Localize(operation=="repair"?"repair":"update");
        Title="KeyPassXC Cloud Sync — "+heading;
        _title=new TextBlock{Text=heading,FontSize=16,FontWeight=FontWeight.SemiBold};
        _versions=new TextBlock{Text=fromVersion==toVersion?fromVersion:fromVersion+" → "+toVersion,
            FontSize=12};
        _brand=new Image{Width=32,Height=32,VerticalAlignment=VerticalAlignment.Center};
        var labels=new StackPanel{Spacing=2,VerticalAlignment=VerticalAlignment.Center};
        labels.Children.Add(_title);labels.Children.Add(_versions);
        var header=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};
        header.Children.Add(_brand);header.Children.Add(labels);
        _phase=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=13};
        _detail=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=12,MaxHeight=48};
        _progress=new ProgressBar{Minimum=0,Maximum=100,Height=8,IsIndeterminate=true};
        _button=new Button{Content=UiKit.IconLabel(Localize("cancel"),icon:"cancel"),
            MinWidth=112,HorizontalAlignment=HorizontalAlignment.Right,
            HorizontalContentAlignment=HorizontalAlignment.Center};
        _button.Click+=(_,_)=>OnButton();
        var content=new StackPanel{Spacing=10};
        content.Children.Add(header);
        content.Children.Add(_phase);
        content.Children.Add(_progress);
        content.Children.Add(_detail);
        content.Children.Add(_button);
        Content=new Border{Margin=new Thickness(10),Padding=new Thickness(14,12),
            CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1),Child=content};
        Closing+=(_,args)=>{if(_working)args.Cancel=true;};
        Opened+=(_,_)=>RefreshTheme();
        ActualThemeVariantChanged+=(_,_)=>RefreshTheme();
        SetProgress(new("preparing"));
    }

    private string Localize(string key)=> (key,_language) switch
    {
        ("repair","uk")=>"Відновлення програми",("repair","en")=>"Application recovery",
        ("repair",_)=>"Восстановление программы",
        ("update","uk")=>"Оновлення програми",("update","en")=>"Application update",
        ("update",_)=>"Обновление программы",
        ("cancel","uk")=>"Скасувати",("cancel","en")=>"Cancel",("cancel",_)=>"Отмена",
        ("close","uk")=>"Закрити",("close","en")=>"Close",("close",_)=>"Закрыть",
        ("preparing","uk")=>"Підготовка операції…",("preparing","en")=>"Preparing operation…",
        ("preparing",_)=>"Подготовка операции…",
        ("checking","uk")=>"Перевірка релізу…",("checking","en")=>"Checking release…",
        ("checking",_)=>"Проверка релиза…",
        ("downloading","uk")=>"Завантаження релізу…",("downloading","en")=>"Downloading release…",
        ("downloading",_)=>"Загрузка релиза…",
        ("verifying","uk")=>"Перевірка цілісності та підпису…",
        ("verifying","en")=>"Verifying integrity and signature…",
        ("verifying",_)=>"Проверка целостности и подписи…",
        ("extracting","uk")=>"Розпакування перевірених файлів…",
        ("extracting","en")=>"Extracting verified files…",
        ("extracting",_)=>"Распаковка проверенных файлов…",
        ("ready","uk")=>"Підготовку завершено",("ready","en")=>"Preparation complete",
        ("ready",_)=>"Подготовка завершена",
        ("stopping","uk")=>"Завершення фонової синхронізації…",
        ("stopping","en")=>"Stopping background synchronization…",
        ("stopping",_)=>"Завершение фоновой синхронизации…",
        ("handoff","uk")=>"Запуск установника і закриття програми…",
        ("handoff","en")=>"Starting installer and closing the application…",
        ("handoff",_)=>"Запуск установщика и закрытие программы…",
        ("waiting","uk")=>"Очікування завершення програми…",
        ("waiting","en")=>"Waiting for the application to exit…",
        ("waiting",_)=>"Ожидание завершения программы…",
        ("installing","uk")=>"Заміна файлів програми…",
        ("installing","en")=>"Replacing application files…",
        ("installing",_)=>"Замена файлов программы…",
        ("testing","uk")=>"Перевірка встановленої версії…",
        ("testing","en")=>"Testing the installed version…",
        ("testing",_)=>"Проверка установленной версии…",
        ("rollback","uk")=>"Повернення попередньої версії…",
        ("rollback","en")=>"Rolling back to the previous version…",
        ("rollback",_)=>"Откат к предыдущей версии…",
        ("starting","uk")=>"Запуск програми…",("starting","en")=>"Starting the application…",
        ("starting",_)=>"Запуск программы…",
        ("update-complete","uk")=>"Оновлення успішно встановлено",
        ("update-complete","en")=>"Update installed successfully",
        ("update-complete",_)=>"Обновление успешно установлено",
        ("repair-complete","uk")=>"Програму успішно відновлено",
        ("repair-complete","en")=>"Application restored successfully",
        ("repair-complete",_)=>"Программа успешно восстановлена",
        ("repair-rollback","uk")=>"Відновлення не вдалося. Попередній стан повернено.",
        ("repair-rollback","en")=>"Recovery failed. The previous state was restored.",
        ("repair-rollback",_)=>"Восстановление не удалось. Предыдущее состояние возвращено.",
        ("rolled-back","uk")=>"Оновлення не вдалося. Попередню версію відновлено.",
        ("rolled-back","en")=>"Update failed. The previous version was restored.",
        ("rolled-back",_)=>"Обновление не удалось. Предыдущая версия восстановлена.",
        ("failed","uk")=>"Операцію не завершено. Перевірте стан програми.",
        ("failed","en")=>"Operation failed. Check the application state.",
        ("failed",_)=>"Операция не завершена. Проверьте состояние программы.",
        ("cancelled","uk")=>"Операцію скасовано",("cancelled","en")=>"Operation cancelled",
        ("cancelled",_)=>"Операция отменена",
        _=>key
    };

    private void SetDetail(string? value)
    {
        _detail.Text=value??"";
        _detail.IsVisible=!string.IsNullOrWhiteSpace(value);
    }

    private void RefreshTheme()
    {
        var dark=ActualThemeVariant==ThemeVariant.Dark;
        Background=new SolidColorBrush(Color.Parse(dark?"#101821":"#F4F7FB"));
        Foreground=new SolidColorBrush(Color.Parse(dark?"#E9F0F9":"#182B40"));
        if(Content is Border border)
        {
            border.Background=new SolidColorBrush(Color.Parse(dark?"#182633":"#FFFFFF"));
            border.BorderBrush=new SolidColorBrush(Color.Parse(dark?"#35495C":"#DBE4EE"));
        }
        var secondary=new SolidColorBrush(Color.Parse(dark?"#A2B6C9":"#64788D"));
        _versions.Foreground=secondary;
        _detail.Foreground=secondary;
        BrandArtwork.Apply(_brand,dark,RenderScaling,32);
    }

    internal void SetProgress(UpdateOperationProgress status)
    {
        if(!Dispatcher.UIThread.CheckAccess())
        { Dispatcher.UIThread.Post(()=>SetProgress(status));return; }
        _phase.Text=Localize(status.Phase);
        SetDetail(status.Percent is { } percent
            ?percent.ToString(System.Globalization.CultureInfo.InvariantCulture)+"%"+
                (status.Detail.Length==0?"":" · "+status.Detail)
            :status.Detail);
        _progress.IsIndeterminate=status.Percent is null;
        if(status.Percent is { } value)_progress.Value=Math.Clamp(value,0,100);
        if(_helperMode && !_reactivatedAfterOwnerExit &&
            UpdateHelperForegroundPolicy.AfterOwnerExit(status.Phase))
        {
            _reactivatedAfterOwnerExit=true;
            RequestHelperForeground();
        }
    }

    // The main window may still own foreground when the helper opens. Give the
    // helper a short topmost pulse now and again after the old process exits.
    // Neither event leaves the updater permanently topmost.
    private async void RequestHelperForeground()
    {
        if(!_helperMode || !IsVisible)return;
        var request=++_foregroundRequest;
        try
        {
            Topmost=true;
            Activate();
            UpdateHelperBootstrap.ActivateWindow?.Invoke(this);
            await Task.Delay(700);
        }
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {
            // A denied foreground request must not stop an authenticated update.
        }
        finally
        {
            if(request==_foregroundRequest && IsVisible)Topmost=false;
        }
    }

    internal void DisableCancellation()
    {
        _canCancel=false;
        _button.IsEnabled=false;
    }

    internal async Task<bool> RunPreparationAsync(Window owner,Func<CancellationToken,Task<bool>> action)
    {
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _canCancel=true;
        bool completed=false;
        Opened+=async (_,_)=>
        {
            _working=true;
            try
            {
                await Task.Yield();
                completed=await action(_cancellation.Token);
                if(completed) { _working=false;Close(); }
                else ShowFailure(Localize("failed"));
            }
            catch(OperationCanceledException) when(_cancellation.IsCancellationRequested)
            { ShowFailure(Localize("cancelled")); }
            catch(Exception ex) { ShowFailure(ex.Message); }
            finally { _working=false; }
        };
        await ShowDialog(owner);
        return completed;
    }

    private void ShowFailure(string message)
    {
        _finished=true;
        _canCancel=false;
        _progress.IsVisible=false;
        _phase.Text=message;
        SetDetail(null);
        _button.Content=UiKit.IconLabel(Localize("close"),icon:"dismiss");
        _button.IsEnabled=true;
    }

    internal static async Task ShowResultAsync(Window owner,UpdateOperationResult result)
    {
        var window=new UpdateOperationWindow(result.Operation,result.FromVersion,result.ToVersion)
        {
            WindowStartupLocation=WindowStartupLocation.CenterOwner,
            _finished=true
        };
        window._phase.Text=window.Localize(result.Status switch
        {
            "installed" when result.Operation=="repair"=>"repair-complete",
            "installed"=>"update-complete",
            "rolled-back" when result.Operation=="repair"=>"repair-rollback",
            "rolled-back"=>"rolled-back",
            _=>"failed"
        });
        window.SetDetail(result.Detail);
        window._progress.IsVisible=false;
        window._button.Content=UiKit.IconLabel(window.Localize("close"),icon:"dismiss");
        await window.ShowDialog(owner);
    }

    internal static UpdateOperationWindow Worker(string[] args)
    {
        var recovery=args[0]=="--recover-update";
        var stage=args.Length>(recovery?1:2)?args[recovery?1:2]:"";
        string ReadMeta(string name,string fallback)
        {
            try {return File.ReadAllText(Path.Combine(stage,name)).Trim();}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
            {return fallback;}
        }
        var kind=ReadMeta("operation.txt","update");
        if(kind is not ("repair" or "update"))kind="update";
        var from=ReadMeta("source-version.txt",BuildInfo.Version);
        var to=ReadMeta("verified.txt",BuildInfo.Version).TrimStart('v');
        var lang=ReadMeta("operation-language.txt","auto");
        var window=new UpdateOperationWindow(kind,from,to,lang);
        window._helperMode=true;
        window._button.IsVisible=false;
        window.Opened+=async (_,_)=>
        {
            window.RequestHelperForeground();
            window._working=true;
            UpdateHelperBootstrap.WorkStarted=true;
            await Task.Yield();
            IProgress<UpdateOperationProgress> progress=new Progress<UpdateOperationProgress>(window.SetProgress);
            int code;
            try
            {
                code=await Task.Run(()=>UpdateHelperBootstrap.Run(args,progress.Report));
            }
            catch(Exception ex)
            {
                window.SetProgress(new("failed",null,ex.Message));
                code=1;
            }
            Environment.ExitCode=code;
            window._working=false;
            if(code!=0)
            {
                window.ShowFailure(window.Localize("failed"));
                await Task.Delay(5000);
            }
            window.Close();
        };
        return window;
    }

    private void OnButton()
    {
        if(_finished) {Close();return;}
        if(_canCancel)
        {
            DisableCancellation();
            _cancellation.Cancel();
        }
    }
}
