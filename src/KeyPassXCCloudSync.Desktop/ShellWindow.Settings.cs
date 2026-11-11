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
    private async Task ToggleSyncAsync()
    {
        if(_switchingSync || _saving || _testing)return;
        var host=_app.Engine;
        if(host is null || host.Current is null)
        {
            await MessageDialog.Info(this,"Сначала настройте KeyPassXC Cloud Sync",
                "Сохраните путь к KDBX, каталог данных и хотя бы одно облако.");
            return;
        }
        _switchingSync=true;RefreshCurrentPage();
        try
        {
            if(host.SyncEnabled)
            {
                if(!await MessageDialog.AskAction(this,"Опасная операция — остановка синхронизации",
                    UiLang.T("ОСТАНОВИТЬ синхронизацию только на этом компьютере?\n\n")+
                    UiLang.T("После остановки изменения KDBX не передаются в облака и не загружаются оттуда. ") +
                    UiLang.T("Обмен топологией и статистикой продолжится, другие ПК продолжат работу. ") +
                    UiLang.T("Версии KDBX могут разойтись.\n\n")+
                    UiLang.T("Перед повторным включением потребуется новая проверка конфликтов."),
                    "Остановить синхронизацию",danger:true))return;
                await host.Gate.WaitAsync();
                try
                {
                    SettingsStore.SetSyncEnabled(_configPath,false);
                    host.ApplyConfig(AppOptions.Load(_configPath));
                    _draft.SyncEnabled=false;
                }
                finally{host.Gate.Release();}
                _lastResult="KDBX на паузе; обмен топологией и статистикой продолжается.";
            }
            else
            {
                // Unsaved path edits must never be used for a misleading preflight.
                var saved=AppOptions.Load(_configPath);
                if(!SettingsStore.SamePath(_draft.LocalFile,saved.LocalFile) ||
                   !SettingsStore.SamePath(_draft.DataDirectory,saved.DataDirectory))
                {
                    await MessageDialog.Info(this,"Несохранённые пути",
                        "Сначала сохраните изменённые пути KDBX и каталога данных.");
                    return;
                }
                _lastResult="Проверка облаков перед включением…";
                await host.Gate.WaitAsync();
                PreflightReport report;
                try{report=await SyncPreflight.CheckAsync(saved,CancellationToken.None);}
                finally{host.Gate.Release();}
                if(report.Outcome==PreflightOutcome.Undetermined)
                {
                    await MessageDialog.Info(this,"Включение запрещено: проверка неполная",report.Detail);
                    return;
                }
                var conflict=report.Outcome==PreflightOutcome.Conflict;
                var partial=report.UnavailableClouds>0;
                var heading=conflict?"КОНФЛИКТ БУДЕТ — подтвердите запуск":
                    partial?"ЧАСТИЧНАЯ СИНХРОНИЗАЦИЯ — подтвердите запуск":
                    "КОНФЛИКТА НЕ ОБНАРУЖЕНО — подтвердите запуск";
                var details=report.Detail+"\n\n"+
                    (conflict?UiLang.T("ВНИМАНИЕ: конфликтующие версии будут сохранены отдельно; автоматического объединения и перезаписи не будет.\n\n"):"")+
                    UiLang.T("Включить синхронизацию на этом компьютере?");
                if(!await MessageDialog.AskAction(this,heading,details,
                    "Включить синхронизацию",danger:conflict || partial))return;
                await host.Gate.WaitAsync();
                try
                {
                    var current=AppOptions.Load(_configPath);
                    if(current.SyncEnabled)throw new SyncSafetyException("Переключатель изменён в другом процессе.");
                    if(!SettingsStore.SamePath(saved.LocalFile,current.LocalFile) ||
                       !SettingsStore.SamePath(saved.DataDirectory,current.DataDirectory) ||
                       saved.Clouds.Count!=current.Clouds.Count ||
                       saved.Clouds.Where((cloud,index)=>cloud!=current.Clouds[index]).Any())
                        throw new SyncSafetyException("Настройки облаков изменились во время подтверждения. Повторите проверку.");
                    var finalReport=await SyncPreflight.CheckAsync(current,CancellationToken.None);
                    if(finalReport.Outcome!=report.Outcome ||
                       finalReport.VerifiedClouds!=report.VerifiedClouds ||
                       finalReport.UnavailableClouds!=report.UnavailableClouds ||
                       finalReport.Snapshot!=report.Snapshot)
                        throw new SyncSafetyException(UiLang.T("Состояние KDBX или облаков изменилось после первого анализа. Включение отменено — повторите проверку и подтверждение.\n\n")+finalReport.Detail);
                    SettingsStore.SetSyncEnabled(_configPath,true);
                    host.ApplyConfig(AppOptions.Load(_configPath));
                    _draft.SyncEnabled=true;
                    _lastResult=UiLang.T(report.UnavailableClouds>0
                        ?"Синхронизация включена частично. Недоступные облака будут проверяться повторно."
                        :"Синхронизация включена после двух успешных сверок.");
                }
                finally{host.Gate.Release();}
            }
            _app.UpdateTray(_state);
        }
        catch(Exception e){await MessageDialog.Info(this,"Изменение синхронизации не выполнено",UiErrorDisplay.Present(e));}
        finally{_switchingSync=false;RefreshCurrentPage();}
    }

    private Control BuildSettings()
    {
        var root=Column(17);
        root.Children.Add(Text("Настройки",27,true));
        root.Children.Add(Muted("Две независимые группы: общие параметры распространяются через топологию; локальные действуют только на этом компьютере."));

        var local=Column(13);
        local.Children.Add(Text("Файлы и система",16,true));
        local.Children.Add(Muted("Хранятся только на этом ПК. Не передаются другим компьютерам.",12));
        var file=UiKit.Input(_draft.LocalFile,DesktopPlatform.Current.VaultPathExample);
        file.IsEnabled=_app.Engine?.SyncEnabled!=true;
        file.TextChanged+=(_,_)=>_draft.LocalFile=file.Text??"";
        var fileRow=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto,Auto")};
        fileRow.Children.Add(file);
        var pickFile=GridButton("Выбрать…",1,()=>_=ChooseDatabaseAsync());
        var pickDestination=GridButton("Новый путь…",2,()=>_=ChooseNewDatabasePathAsync());
        pickFile.IsEnabled=pickDestination.IsEnabled=_app.Engine?.SyncEnabled!=true;
        fileRow.Children.Add(pickFile);fileRow.Children.Add(pickDestination);
        local.Children.Add(UiKit.Field("Рабочая KDBX на этом ПК",fileRow));
        var dir=UiKit.Input(_draft.DataDirectory,DesktopPlatform.Current.DataPathExample);
        dir.IsEnabled=_app.Engine?.SyncEnabled!=true;
        dir.TextChanged+=(_,_)=>_draft.DataDirectory=dir.Text??"";
        var folderRow=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto")};
        folderRow.Children.Add(dir);
        var pickFolder=GridButton("Выбрать…",1,()=>_=ChooseDataFolderAsync());
        pickFolder.IsEnabled=_app.Engine?.SyncEnabled!=true;
        folderRow.Children.Add(pickFolder);
        local.Children.Add(UiKit.Field("Каталог состояния и журналов на этом ПК",folderRow));
        local.Children.Add(Muted(_app.Engine?.SyncEnabled==true
            ?"Пути заблокированы, пока синхронизация включена. Сначала выключите её на вкладке «Состояние»."
            :"Для нового ПК можно выбрать НЕСУЩЕСТВУЮЩИЙ KDBX через «Новый путь…». При смене каталога данные копируются с проверкой; старый каталог сохраняется.",11));
        local.Children.Add(NumberInput(_draft.Stable,n=>_draft.Stable=n,"Проверка стабильности файла, мс",100,60000));
        local.Children.Add(Text("Локальные резервные копии",16,true));
        var backupAtLaunch=new CheckBox{
            Content=UiLang.T("Делать резервную копию при старте"),
            IsChecked=_draft.BackupOnStartup};
        backupAtLaunch.IsCheckedChanged+=(_,_)=>_draft.BackupOnStartup=backupAtLaunch.IsChecked==true;
        local.Children.Add(backupAtLaunch);
        local.Children.Add(NumberInput(_draft.StartupBackupKeep,n=>_draft.StartupBackupKeep=n,
            "Хранить последние NNN резервных копий",1,999));
        local.Children.Add(Muted("Копии при старте хранятся только на этом ПК в папке startup-backups внутри каталога состояния. " +
            "Формат: Имя-YYYY-MM-DD-01.kdbx … -99.kdbx. При ошибке появится предупреждение. " +
            "Старые копии удаляются только после проверки новой.",11));
        local.Children.Add(Text("Поведение компьютера",16,true));
        local.Children.Add(NumberInput(_draft.Offline,n=>_draft.Offline=n,"Локальное предупреждение об офлайне, мин",1,10080));
        var auto=new CheckBox{Content=UiLang.T("Запускать KeyPassXC Cloud Sync при входе в Windows"),
            IsChecked=SettingsStore.AutostartEnabled()};
        auto.IsCheckedChanged+=(_,_)=>TryAutostart(auto.IsChecked==true);
        local.Children.Add(auto);
        var rememberWindow=new CheckBox{
            Content=UiLang.T("Сохранять позицию открытого окна"),
            IsChecked=_draft.RememberWindowPosition};
        rememberWindow.IsCheckedChanged+=(_,_)=>_draft.RememberWindowPosition=rememberWindow.IsChecked==true;
        local.Children.Add(rememberWindow);
        local.Children.Add(Muted("Язык и тема интерфейса в верхней панели — тоже локальные настройки. Реквизиты облаков хранятся только на этом ПК.",11));
        var shared=Column(13);
        shared.Children.Add(Muted("Публикуются в topology.json и автоматически применяются на остальных компьютерах после получения обновления.",12));
        shared.Children.Add(Text("Синхронизация",16,true));
        shared.Children.Add(NumberInput(_draft.Debounce,n=>_draft.Debounce=n,
            "Ожидание после изменения KDBX, мс",0,600000));
        shared.Children.Add(NumberInput(_draft.Retry,n=>_draft.Retry=n,
            "Повтор после ошибки, с",1,3600));
        shared.Children.Add(NumberInput(_draft.Poll,n=>_draft.Poll=n,
            "Интервал опроса по умолчанию для новых облаков, с",10,86400));
        shared.Children.Add(Muted("Интервал опроса, тайм-аут и параметры кэша каждого облака — общие для всех ПК и изменяются на вкладке «Облака».",11));
        shared.Children.Add(Text("История и статистика",16,true));
        shared.Children.Add(NumberInput(_draft.StatsPublish,n=>_draft.StatsPublish=n,
            "Публикация общей статистики, мин",1,10080));
        shared.Children.Add(NumberInput(_draft.Copies,n=>_draft.Copies=n,
            "Минимум резервных версий KDBX",20,10000));
        shared.Children.Add(NumberInput(_draft.Days,n=>_draft.Days=n,
            "Минимальный срок хранения версий, дней",30,3650));
        shared.Children.Add(Muted("Список компьютеров, список облаков и интервалы их опроса — также общие. Изменяются во вкладках «Компьютеры» и «Облака».",12));
        var activeComputers=0;
        try
        {
            var cfg=_app.Engine?.Current;
            if(cfg is not null)activeComputers=TopologyCoordinator.Load(cfg)?.Computers.Count(c=>c.Active)??0;
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
        catch(System.Text.Json.JsonException){}catch(SyncSafetyException){}
        shared.Children.Add(Muted(UiLang.T("В топологии: облаков — ")+_draft.Clouds.Count+
            UiLang.T(", активных компьютеров — ")+activeComputers+".",12));
        foreach(var c in _draft.Clouds)
            shared.Children.Add(Muted(c.Name+UiLang.T(": опрос каждые ")+FormatInterval(c.Poll),11));
        shared.Children.Add(Muted("Общие настройки становятся доступны остальным ПК после публикации топологии; при отсутствии сети изменение останется ожидающим.",11));

        var columns=new Grid{ColumnDefinitions=new ColumnDefinitions("*,*"),ColumnSpacing=16};
        columns.Children.Add(Section("Локальные · этот компьютер",local));
        var commonCard=Section("Общие · все компьютеры",shared);
        Grid.SetColumn(commonCard,1);columns.Children.Add(commonCard);
        root.Children.Add(columns);
        root.Children.Add(Row(Action(_saving?"Сохранение...":"Сохранить настройки",()=>_=SaveAsync(),true),
            Action("Перечитать сохранённые",()=>ReloadDraft())));
        return Wrap(root);
    }
    private Control GridButton(string name,int column,Action action)
    {
        var b=Action(name,action);b.Margin=new Thickness(8,0,0,0);Grid.SetColumn(b,column);return b;
    }
    private Control NumberInput(int current,Action<int> setter,string name,int min,int max)
    {
        var t=UiKit.Input(current.ToString());
        t.TextChanged+=(_,_)=>{if(int.TryParse(t.Text,out var n)&&n>=min&&n<=max)setter(n);};
        return UiKit.Field(name,t);
    }
    private async Task ChooseDatabaseAsync()
    {
        if(_app.Engine?.SyncEnabled==true)return;
        try
        {
            var provider=StorageProvider;
            var found=await provider.OpenFilePickerAsync(new FilePickerOpenOptions{
                Title=UiLang.T("Выберите базу KeePassXC"),AllowMultiple=false,
                FileTypeFilter=[new FilePickerFileType("KeePass database"){Patterns=["*.kdbx"]}]});
            if(found.Count>0 && found[0].TryGetLocalPath() is {Length:>0} local)
            { _draft.LocalFile=local;RefreshCurrentPage(); }
        }
        catch(Exception e){ShowProblem("Выбор файла",e.Message);}
    }
    private async Task ChooseNewDatabasePathAsync()
    {
        if(_app.Engine?.SyncEnabled==true)return;
        try
        {
            var chosen=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title=UiLang.T("Укажите путь к будущей KDBX (создавать её не нужно)"),
                SuggestedFileName=Path.GetFileName(_draft.LocalFile) is {Length:>0} name?name:"Passwords.kdbx",
                DefaultExtension="kdbx",
                FileTypeChoices=[new FilePickerFileType("KeePassXC (*.kdbx)"){Patterns=["*.kdbx"]}]
            });
            if(chosen?.TryGetLocalPath() is {Length:>0} local)
            {
                _draft.LocalFile=local;RefreshCurrentPage();
            }
        }
        catch(Exception ex){ShowProblem("Путь новой KDBX",ex.Message);}
    }
    private async Task ChooseDataFolderAsync()
    {
        if(_app.Engine?.SyncEnabled==true)return;
        try
        {
            var found=await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions{Title=UiLang.T("Выберите папку данных KeyPassXC Cloud Sync"),AllowMultiple=false});
            if(found.Count>0 && found[0].TryGetLocalPath() is {Length:>0} local)
            { _draft.DataDirectory=local;RefreshCurrentPage(); }
        }
        catch(Exception e){ShowProblem("Выбор каталога",e.Message);}
    }
    private async Task<bool> SaveAsync(bool quiet=false)
    {
        if(_saving || _testing || _switchingSync)return false;
        _saving=true;
        IDatabaseReservation? reservation=null;
        var host=_app.Engine!;
        try
        {
            if(host.SyncEnabled &&
                (!SettingsStore.SamePath(host.Current!.LocalFile,_draft.LocalFile) ||
                 !SettingsStore.SamePath(host.Current!.DataDirectory,_draft.DataDirectory)))
                throw new SyncSafetyException("Путь KDBX и каталог данных можно менять только при выключенной синхронизации.");
            reservation=host.ReserveFile(_draft.LocalFile);
            await host.Gate.WaitAsync();
            try
            {
                var previous=host.Current?.DataDirectory;
                var cfg=await Task.Run(()=>SettingsStore.Save(_draft,_configPath,previous));
                host.ApplyConfig(cfg,reservation);
                reservation=null;
            }
            finally{host.Gate.Release();}
            _draft=SettingsDraft.Load(_configPath);
            UpdateWindowPlacementPreference(_draft.RememberWindowPosition);
            UiLang.Use(_draft.Language);
            _app.SetTheme(_draft.Theme);
            _lastResult=host.SyncEnabled?"Настройки сохранены. Цикл синхронизации возобновлён.":
                "Настройки сохранены. Синхронизация выключена — сначала выполните проверку и включение.";
            if(!quiet)await MessageDialog.Info(this,"Сохранено",_lastResult);
            return true;
        }
        catch(Exception e)
        {
            await MessageDialog.Info(this,"Ошибка сохранения",UiErrorDisplay.Present(e));
            return false;
        }
        finally
        {
            host.ReleaseReservation(reservation);
            _saving=false;
            RefreshCurrentPage();
        }
    }
    private void ReloadDraft()
    {
        try{_draft=SettingsDraft.Load(_configPath);UpdateWindowPlacementPreference(_draft.RememberWindowPosition);UiLang.Use(_draft.Language);_app.SetTheme(_draft.Theme);RefreshCurrentPage();}
        catch(Exception e){ShowProblem("Чтение настроек",e.Message);}
    }
    private void TryAutostart(bool enable)
    {
        try{SettingsStore.SetAutostart(enable,_configPath);}
        catch(Exception e){ShowProblem("Автозапуск Windows",e.Message);}
    }
    // Six diagnostics actions share one row, with the standalone Windows
    // notification test at the far right. Compact button-only typography keeps
    // the group within the supported compact window width.
    // Every remote test uses an isolated, uniquely owned test directory.
}
