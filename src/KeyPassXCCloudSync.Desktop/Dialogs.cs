using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace KeyPassXCCloudSync;

internal static class UiKit
{
    internal static readonly Thickness Pad=new(18);
    // KeePassXC Card Print: Border.settings-card CornerRadius=4 DIP.
    // Applies only to the content panels inside the seven pages.
    internal const double ContentCardCornerRadius=4;
    internal const int NavigationIconSize=14;
    internal static bool Dark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
    internal static IBrush DialogBackground => new SolidColorBrush(Color.Parse(Dark ? "#172333" : "#F8FAFC"));
    internal static IBrush LabelColor => new SolidColorBrush(Color.Parse(Dark ? "#EFF5FF" : "#1D3145"));
    internal static TextBlock Text(string s,int size=14,bool bold=false,IBrush? brush=null)=>new()
    {Text=UiLang.T(s),FontSize=size,FontWeight=bold?FontWeight.SemiBold:FontWeight.Normal,Foreground=brush??LabelColor,
        TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    // The SAME Microsoft Fluent System Icons package/version as Card Print.
    // Icons are regular 20px glyphs, independently centered in every button.
    internal static StackPanel IconLabel(string label,string? icon=null,bool primary=false,bool navigation=false)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=7,
            HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        row.Children.Add(IconGlyph(icon??FluentUiIcons.ForAction(label),
            navigation?NavigationIconSize:16,primary,navigation));
        var caption=new TextBlock{Text=UiLang.T(label),
            FontSize=navigation?UiTypography.NavigationSize:UiTypography.CaptionSize,
            VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.NoWrap};
        if(!navigation)caption.Foreground=primary?Brushes.White:LabelColor;
        row.Children.Add(caption);
        return row;
    }
    internal static FluentIcons.Avalonia.FluentIcon IconGlyph(string icon,int size=18,
        bool primary=false,bool navigation=false)
        =>FluentUiIcons.Create(icon,size,primary?Brushes.White:
            navigation?new SolidColorBrush(ReferenceTabPalette.For(Dark).Text):LabelColor);
    internal static Button Button(string label,Action action,bool primary=false,string? icon=null,bool danger=false)
    {
        var b=new Button{Content=IconLabel(label,icon,primary||danger),Padding=new Thickness(13,9),MinHeight=38,
            HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};
        if(primary||danger)
        {
            b.Background=new SolidColorBrush(Color.Parse(danger?"#BB3548":"#2876DB"));
            b.Foreground=Brushes.White;
        }
        b.Click+=(_,_)=>action();return b;
    }
    internal static StackPanel Stack(int space=12,Orientation orientation=Orientation.Vertical)
        =>new(){Orientation=orientation,Spacing=space};
    internal static TextBox Input(string value,string placeholder="")=>new()
    {Text=value,PlaceholderText=UiLang.T(placeholder),MinHeight=36,HorizontalAlignment=HorizontalAlignment.Stretch};
    internal static StackPanel Field(string caption,Control input)
    {
        var st=Stack(7);st.Children.Add(Text(caption,13,true));st.Children.Add(input);return st;
    }
    internal static Border Card(Control content,bool dark=false)
        =>new(){Background=new SolidColorBrush(Color.Parse(dark?"#1D2B3B":"#FFFFFF")),
            BorderBrush=new SolidColorBrush(Color.Parse(dark?"#324457":"#E1E9F2")),BorderThickness=new Thickness(1),
            CornerRadius=new CornerRadius(ContentCardCornerRadius),Padding=Pad,Child=content};
    internal static void OpenFolder(string path)
    {
        PlatformServices.Current.Processes.OpenFolder(path);
    }
}

// Common modal shell copied in layout/semantics from the supplied Card Print sample.
// Never return true for Esc, the title-bar X, or any window-close failure.
internal sealed class MessageDialog : Window
{
    private bool _accepted;
    private void Finish(bool accepted){_accepted=accepted;Close(accepted);}
    private MessageDialog(string heading,string body,bool confirm,bool danger=false,string? approvalText=null)
    {
        Title=UiLang.T(heading);
        Width=485;MinWidth=390;SizeToContent=SizeToContent.Height;MaxHeight=560;
        CanResize=false;CanMinimize=false;ShowInTaskbar=false;
        WindowDecorations=WindowDecorations.Full;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=UiKit.DialogBackground;
        AppWindowIcons.Attach(this);
        var root=new DockPanel{LastChildFill=true};
        var actions=UiKit.Stack(7,Orientation.Horizontal);
        actions.HorizontalAlignment=HorizontalAlignment.Right;
        actions.VerticalAlignment=VerticalAlignment.Center;
        var approve=UiKit.Button(approvalText??(confirm?"Подтвердить":"Понятно"),
            ()=>Finish(true),primary:!danger,danger:danger,
            icon:confirm?(danger?"warning":"check"):"check");
        actions.Children.Add(approve);
        if(confirm)actions.Children.Add(UiKit.Button("Отмена",()=>Finish(false),icon:"cancel"));
        var footer=new Border
        {
            Margin=new Thickness(12,0,12,12),Padding=new Thickness(10,8),
            Background=new SolidColorBrush(Color.Parse(UiKit.Dark?"#102133":"#EEF2F6")),
            Child=actions
        };
        DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var headingRow=UiKit.Stack(9,Orientation.Horizontal);
        headingRow.Children.Add(UiKit.IconGlyph(danger?"warning":confirm?"info":"info",19));
        headingRow.Children.Add(UiKit.Text(heading,15,true));
        var text=UiKit.Stack(10);
        text.Children.Add(headingRow);
        text.Children.Add(UiKit.Text(body,13));
        var card=UiKit.Card(text,UiKit.Dark);
        card.Padding=new Thickness(14,12);
        card.Margin=new Thickness(12);
        root.Children.Add(new ScrollViewer{Content=card,MaxHeight=420,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        Content=root;
        KeyDown+=(_,e)=>{if(e.Key==Avalonia.Input.Key.Escape)
            {e.Handled=true;Finish(false);}};
    }
    internal static Task<bool> Ask(Window owner,string title,string message,bool danger=false)
        =>new MessageDialog(title,message,true,danger).ShowDialog<bool>(owner);
    internal static Task<bool> AskAction(Window owner,string title,string message,string accept,bool danger=false)
        =>new MessageDialog(title,message,true,danger,accept).ShowDialog<bool>(owner);
    internal static Task<bool> Info(Window owner,string title,string message)
        =>new MessageDialog(title,message,false).ShowDialog<bool>(owner);
    internal static Task<bool> AskStandalone(string title,string message,bool danger=false)
    {
        var dialog=new MessageDialog(title,message,true,danger)
        {
            WindowStartupLocation=WindowStartupLocation.CenterScreen,
            Topmost=true,ShowInTaskbar=true
        };
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed+=(_,_)=>completion.TrySetResult(dialog._accepted);
        dialog.Opened+=(_,_)=>dialog.Activate();
        dialog.Show();
        return completion.Task;
    }
}

internal sealed class CloudDialog : Window
{
    private readonly CloudDraft _draft;
    private readonly TextBox _name,_remote,_account,_clientId,_secret,_url,_user,_password,_executable,_timeout,_folderCache,_s3Endpoint,_s3Bucket,_s3Region,_s3Id,_s3Secret,_poll;
    private readonly StackPanel _google,_webdav,_proton,_s3;
    internal CloudDialog(string provider,CloudDraft? source=null,int defaultPoll=60,string? configPath=null)
    {
        _draft=source?.Clone()??new CloudDraft{Provider=provider,Name=SuggestName(provider),
            RefreshTarget="KeePassSync/Google/"+Guid.NewGuid().ToString("N")+"/refresh-token"};
        if(source is null)_draft.Poll=defaultPoll;
        if(source is null && provider=="WebDAV")_draft.CredentialTarget="KeePassSync/WebDAV/"+Guid.NewGuid().ToString("N");
        if(source is null && (provider is "S3" or "CloudflareR2"))
        {
            _draft.CredentialTarget="KeePassSync/S3/"+Guid.NewGuid().ToString("N");
            _draft.Root="";_draft.S3Region=provider=="CloudflareR2"?"auto":"us-east-1";
        }
        Title=UiLang.T(source is null?"Добавить облако":"Изменить подключение");
        Width=690;Height=740;MinWidth=600;MinHeight=580;
        CanMinimize=false;ShowInTaskbar=false;
        Background=UiKit.DialogBackground;
        WindowDecorations=WindowDecorations.Full;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        AppWindowIcons.Attach(this);
        var form=UiKit.Stack(12);
        form.Margin=new Thickness(26,23,26,24);
        form.Children.Add(UiKit.Text(source is null?"Новое подключение":"Параметры подключения",22,true));
        form.Children.Add(UiKit.Text(CloudProviderNames.Display(provider)+" · "+(provider switch
        {
            "GoogleDrive" => "отдельная авторизация каждого аккаунта",
            "WebDAV" => "адрес и пароль приложения",
            "S3" or "CloudflareR2" => "endpoint, bucket, регион подписи SigV4 и ключи",
            _ => "официальный CLI"
        }),13));
        _name=UiKit.Input(_draft.Name,"Например, Home Assistant");
        form.Children.Add(UiKit.Field("Имя подключения",_name));
        _remote=UiKit.Input(_draft.Remote,"KeePassSync");
        form.Children.Add(UiKit.Field("Каталог в облаке",_remote));
        _google=UiKit.Stack(11);
        _google.Children.Add(UiKit.Text("Авторизация выполняется через браузер. Для каждого Google-аккаунта создаётся отдельное подключение.",13));
        _account=UiKit.Input(_draft.Account,"Заполнится после входа");
        _account.IsReadOnly=true;
        _google.Children.Add(UiKit.Field("Подтверждённый аккаунт",_account));
        _clientId=UiKit.Input(_draft.ClientId,"Необязательно для официальной сборки");
        _google.Children.Add(UiKit.Field("Собственный OAuth Client ID (дополнительно)",_clientId));
        _secret=UiKit.Input("","Секрет Desktop OAuth-клиента, если указан свой ID");
        _secret.PasswordChar='●';
        _google.Children.Add(UiKit.Field("Секрет своего OAuth-клиента",_secret));
        form.Children.Add(_google);
        _webdav=UiKit.Stack(11);
        _url=UiKit.Input(_draft.BaseUrl,"https://server.example/remote.php/dav/files/user/");
        _webdav.Children.Add(UiKit.Field("HTTPS WebDAV URL",_url));
        _user=UiKit.Input(_draft.WebUser,"Логин WebDAV");
        _webdav.Children.Add(UiKit.Field("Пользователь",_user));
        _password=UiKit.Input("","Новый пароль приложения (пусто — оставить существующий)");
        _password.PasswordChar='●';_webdav.Children.Add(UiKit.Field("Пароль приложения",_password));
        form.Children.Add(_webdav);
        _proton=UiKit.Stack(11);
        var detectedCli=PlatformServices.Current.Paths.ResolveProtonExecutable("",configPath);
        _executable=UiKit.Input(_draft.Executable,detectedCli.Length>0
            ?UiLang.T("Автоматически: ")+detectedCli:"Автоматически: рядом с программой, затем PATH");
        var cliPathRow=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto,Auto"),ColumnSpacing=7};
        cliPathRow.Children.Add(_executable);
        var browseCli=UiKit.Button("Обзор…",()=>{_ = BrowseProtonCliAsync();},icon:"folder-open");
        Grid.SetColumn(browseCli,1);cliPathRow.Children.Add(browseCli);
        var automaticCli=UiKit.Button("Авто",()=>_executable.Text="",icon:"refresh");
        ToolTip.SetTip(automaticCli,UiLang.T("Вернуть автоматический поиск Proton CLI"));
        Grid.SetColumn(automaticCli,2);cliPathRow.Children.Add(automaticCli);
        _proton.Children.Add(UiKit.Field("Путь к Proton CLI",cliPathRow));
        form.Children.Add(_proton);
        _s3=UiKit.Stack(11);
        _s3.Children.Add(UiKit.Text("Создайте bucket у своего провайдера заранее. Для R2 укажите регион auto. Условные записи должны поддерживаться сервером. Ключи сохраняются в Windows Credential Manager.",13));
        _s3Endpoint=UiKit.Input(_draft.BaseUrl,provider=="CloudflareR2"?"https://ACCOUNT_ID.r2.cloudflarestorage.com":"https://s3.eu-central-1.amazonaws.com");
        _s3.Children.Add(UiKit.Field("Endpoint (HTTPS)",_s3Endpoint));
        _s3Bucket=UiKit.Input(_draft.Root,"my-keepass-sync");
        _s3.Children.Add(UiKit.Field("Bucket",_s3Bucket));
        _s3Region=UiKit.Input(_draft.S3Region,provider=="CloudflareR2"?"auto":"eu-central-1");
        _s3.Children.Add(UiKit.Field("Регион подписи SigV4",_s3Region));
        string savedKey="";
        if(source is not null && (source.Provider is "S3" or "CloudflareR2"))
        {
            try{savedKey=PlatformServices.Current.Secrets.Read(_draft.CredentialTarget).User;}
            catch(SyncSafetyException){} // credentials can be created on save
        }
        _s3Id=UiKit.Input(savedKey,"Access Key ID");
        _s3.Children.Add(UiKit.Field("Access Key ID",_s3Id));
        _s3Secret=UiKit.Input("","Secret Access Key (пусто — оставить сохранённый)");
        _s3Secret.PasswordChar='●';
        _s3.Children.Add(UiKit.Field("Secret Access Key",_s3Secret));
        form.Children.Add(_s3);
        _folderCache=UiKit.Input(_draft.GoogleFolderCacheSeconds.ToString(),"10");
        _google.Children.Add(UiKit.Field("Кэш каталогов Google Drive, секунд (0 — отключить)",_folderCache));
        _poll=UiKit.Input(_draft.Poll.ToString(),"60");
        form.Children.Add(UiKit.Field("Проверять изменения каждые, секунд",_poll));
        _timeout=UiKit.Input(_draft.Timeout.ToString(),"120");
        form.Children.Add(UiKit.Field("Тайм-аут запроса, секунд",_timeout));
        _google.IsVisible=provider=="GoogleDrive";
        _webdav.IsVisible=provider=="WebDAV";
        _proton.IsVisible=provider=="ProtonDrive";
        _s3.IsVisible=provider is "S3" or "CloudflareR2";
        var actions=UiKit.Stack(9,Orientation.Horizontal);actions.HorizontalAlignment=HorizontalAlignment.Right;
        actions.Children.Add(UiKit.Button("Сохранить подключение",()=>
        {
            try
            {
                var name=_name.Text?.Trim()??"";
                if(!CloudConnectionName.IsValid(name))
                    throw new ArgumentException("Имя подключения: буквы, цифры, пробелы, _, -, ., скобки, & или +; от 1 до 80 символов.");
                if(!int.TryParse(_timeout.Text,out var timeout)||timeout<5||timeout>3600)
                    throw new ArgumentException("Тайм-аут должен быть от 5 до 3600 секунд");
                if(!int.TryParse(_poll.Text,out var poll)||poll<10||poll>86400)
                    throw new ArgumentException("Интервал опроса: от 10 до 86400 секунд");
                _draft.Name=name;_draft.Remote=_remote.Text?.Trim()??"";_draft.Timeout=timeout;_draft.Poll=poll;
                if(_draft.Provider=="GoogleDrive")
                {
                    if(!int.TryParse(_folderCache.Text,out var cacheSeconds)||cacheSeconds<0||cacheSeconds>60)
                        throw new ArgumentException("Кэш каталогов: от 0 до 60 секунд; 0 отключает кэш.");
                    _draft.GoogleFolderCacheSeconds=cacheSeconds;
                    _draft.ClientId=_clientId.Text?.Trim()??"";_draft.GoogleSecret=_secret.Text??"";
                    if(_draft.ClientId.Length>0 && _draft.GoogleSecret.Length>0 && _draft.SecretTarget.Length==0)
                        _draft.SecretTarget="KeePassSync/Google/"+Guid.NewGuid().ToString("N")+"/client-secret";
                }
                else if(_draft.Provider=="WebDAV")
                {
                    _draft.BaseUrl=_url.Text?.Trim()??"";_draft.WebUser=_user.Text?.Trim()??"";
                    _draft.WebPassword=_password.Text??"";
                }
                else if(_draft.Provider is "S3" or "CloudflareR2")
                {
                    _draft.BaseUrl=_s3Endpoint.Text?.Trim()??"";
                    _draft.Root=_s3Bucket.Text?.Trim()??"";
                    _draft.S3Region=_s3Region.Text?.Trim()??"";
                    _draft.S3AccessKey=_s3Id.Text?.Trim()??"";
                    _draft.S3SecretKey=_s3Secret.Text??"";
                    if(_draft.Provider=="CloudflareR2" && _draft.S3Region!="auto")
                        throw new ArgumentException("Cloudflare R2: регион подписи должен быть auto");
                    if(!S3Store.ValidBucket(_draft.Root) || !S3Store.ValidRegion(_draft.S3Region))
                        throw new ArgumentException("Неверный bucket или регион S3");
                    if(!Uri.TryCreate(_draft.BaseUrl,UriKind.Absolute,out var s3url) || s3url.Scheme!="https" ||
                       s3url.AbsolutePath!="/" || s3url.UserInfo.Length>0 || s3url.Query.Length>0 || s3url.Fragment.Length>0)
                        throw new ArgumentException("Укажите HTTPS endpoint без пути, параметров и логина");
                    if(_draft.S3SecretKey.Length>0 && _draft.S3AccessKey.Length==0)
                        throw new ArgumentException("Укажите Access Key ID");
                    if(_draft.S3SecretKey.Length==0 && source is null)
                        throw new ArgumentException("При добавлении облака нужен Secret Access Key");
                    if(_draft.S3SecretKey.Length==0 && source is not null &&
                       _draft.S3AccessKey!=savedKey)
                        throw new ArgumentException("Для изменения Access Key ID укажите новый Secret Access Key");
                }
                else
                {
                    _draft.Account=""; // Proton CLI owns the signed-in account.
                    _draft.Executable=_executable.Text?.Trim()??"";
                    if(_draft.Executable.Length>0)
                    {
                        if(!string.Equals(Path.GetFileName(_draft.Executable),"proton-drive.exe",
                            StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException(UiLang.T("Выберите исполняемый файл proton-drive.exe"));
                        var fullPath=PlatformServices.Current.Paths.ResolveProtonExecutable(
                            _draft.Executable,configPath);
                        if(!File.Exists(fullPath))
                            throw new FileNotFoundException(UiLang.T("Файл proton-drive.exe не найден"),fullPath);
                    }
                    // The writable CLI root is fixed at /my-files.
                    _draft.Root=ProtonConfiguration.Root;
                }
                Close(_draft);
            }
            catch(Exception e){_ = MessageDialog.Info(this,"Проверьте поля",UiErrorDisplay.Present(e));}
        },true));
        actions.Children.Add(UiKit.Button("Отмена",()=>Close((CloudDraft?)null),icon:"cancel"));
        // Keep Save/Cancel always visible while the long provider form scrolls.
        var layout=new DockPanel{LastChildFill=true};
        var footer=new Border{Margin=new Thickness(12,0,12,12),Padding=new Thickness(10,8),
            Background=new SolidColorBrush(Color.Parse(UiKit.Dark?"#102133":"#EEF2F6")),Child=actions};
        DockPanel.SetDock(footer,Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer{Content=form,
            VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto});
        Content=layout;
        KeyDown+=(_,e)=>{if(e.Key==Avalonia.Input.Key.Escape)
            {e.Handled=true;Close((CloudDraft?)null);}};
    }
    private async Task BrowseProtonCliAsync()
    {
        try
        {
            var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title=UiLang.T("Выберите исполняемый файл proton-drive.exe"),
                AllowMultiple=false,
                FileTypeFilter=[new FilePickerFileType(UiLang.T("Исполняемые файлы (*.exe)")){Patterns=["*.exe"]}]
            });
            if(files.Count==0)return;
            var filePath=files[0].TryGetLocalPath();
            if(string.IsNullOrWhiteSpace(filePath))
                throw new InvalidOperationException(UiLang.T("Не удалось определить локальный путь к файлу"));
            if(!string.Equals(Path.GetFileName(filePath),"proton-drive.exe",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(UiLang.T("Выберите исполняемый файл proton-drive.exe"));
            _executable.Text=Path.GetFullPath(filePath);
        }
        catch(Exception error)
        {
            await MessageDialog.Info(this,"Выбор файла",UiErrorDisplay.Present(error));
        }
    }
    private static string SuggestName(string provider)=>provider switch
    {"GoogleDrive"=>"Google","WebDAV"=>"NAS","S3"=>"S3","CloudflareR2"=>"R2",_=>"Proton"};
}
