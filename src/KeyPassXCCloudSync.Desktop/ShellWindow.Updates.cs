using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KeyPassXCCloudSync;

internal sealed partial class ShellWindow
{
    private TextBlock? _updateStatus;
    private Button? _checkUpdatesButton, _installUpdateButton, _repairUpdateButton;
    private bool _repairRunning;
    internal bool CanInstallUpdate => !_saving && !_testing && !_switchingSync;

    private Control BuildUpdateSettings()
    {
        var updates = DesktopPlatform.Current.Updates;
        var p = updates.Preferences;
        var root = Column(8);
        root.Children.Add(Text(UiLang.T("Обновления"),16,true));
        var check = new CheckBox { Content=UiLang.T("Автоматически проверять обновления"), IsChecked=p.CheckAutomatically };
        var download = new CheckBox { Content=UiLang.T("Автоматически скачивать обновления"), IsChecked=p.DownloadAutomatically,
            IsEnabled=p.CheckAutomatically };
        check.IsCheckedChanged += (_,_) =>
        {
            updates.SavePreferences(updates.Preferences with { CheckAutomatically=check.IsChecked==true });
            download.IsEnabled=check.IsChecked==true;
        };
        download.IsCheckedChanged += (_,_) =>
            updates.SavePreferences(updates.Preferences with { DownloadAutomatically=download.IsChecked==true });
        root.Children.Add(check);root.Children.Add(download);
        root.Children.Add(Text(UiLang.T("Устанавливать обновление"),13,true));
        var choices = new[]
        {
            ("exit",UiLang.T("При выходе из приложения")),
            ("ask",UiLang.T("Предлагать перезапуск")),
            ("manual",UiLang.T("Только вручную"))
        };
        var modes = new ComboBox
        {
            MinWidth=260,
            ItemsSource=choices.Select(x=>x.Item2).ToArray(),
            SelectedIndex=Array.FindIndex(choices,x=>x.Item1==p.Installation)
        };
        modes.SelectionChanged += (_,_)=>
        {
            if(modes.SelectedIndex>=0)
                updates.SavePreferences(updates.Preferences with { Installation=choices[modes.SelectedIndex].Item1 });
        };
        root.Children.Add(modes);
        _updateStatus = Text("",12,false,Secondary);
        root.Children.Add(_updateStatus);
        _checkUpdatesButton=Action("Проверить обновления",()=>_=CheckForUpdateAsync());
        _installUpdateButton=Action("Установить сейчас",()=>_=ConfirmInstallUpdateAsync());
        _repairUpdateButton=Action("Восстановить…",()=>_=RestoreCurrentVersionAsync());
        root.Children.Add(Row(_checkUpdatesButton,_installUpdateButton,_repairUpdateButton));
        RefreshUpdateStatus();
        return root;
    }

    internal void RefreshUpdateStatus()
    {
        if (_updateStatus is null) return;
        var updates=DesktopPlatform.Current.Updates;
        var state=updates.Snapshot;
        var offerRepair=UpdateActionPolicy.OfferRepair(state,updates.IsReinstallReady);
        if(_checkUpdatesButton is not null)
            _checkUpdatesButton.IsEnabled=!_repairRunning && state.Status!="checking";
        if(_installUpdateButton is not null)
        {
            _installUpdateButton.IsVisible=!offerRepair;
            _installUpdateButton.IsEnabled=!_repairRunning && updates.IsReady &&
                !updates.IsReinstallReady && state.Status=="ready";
        }
        if(_repairUpdateButton is not null)
        {
            _repairUpdateButton.IsVisible=offerRepair;
            _repairUpdateButton.IsEnabled=!_repairRunning && offerRepair;
        }
        var key=state.Status switch
        {
            "checking"=>"Проверка обновлений…",
            "available"=>state.Reinstall?"Доступно восстановление":"Доступна новая версия",
            "ready"=>state.Reinstall?"Восстановление готово":"Обновление готово",
            "current"=>"Установлена актуальная версия",
            "error"=>"Ошибка обновления",
            _=>"Обновления не проверялись"
        };
        _updateStatus.Text=UiLang.T(key)+(state.Version.Length>0?" · "+state.Version:"")+
            (state.Unverified?"\n"+UiLang.T("GitHub Immutable Releases отключён: подлинность обновления не подтверждена."):"")+
            (state.Status=="error"?"\n"+state.Detail:"");
    }

    private async Task CheckForUpdateAsync()
    {
        await _app.CheckUpdatesAsync(true);
        RefreshUpdateStatus();
    }

    private async Task RestoreCurrentVersionAsync()
    {
        var updates=DesktopPlatform.Current.Updates;
        if(_repairRunning || !UpdateActionPolicy.OfferRepair(updates.Snapshot,updates.IsReinstallReady))
            return;
        if(updates.IsReinstallReady)
        {
            await ConfirmInstallUpdateAsync();
            return;
        }
        if(!await MessageDialog.AskAction(this,UiLang.T("Восстановление приложения"),
            UiLang.T("Скачать и проверить официальный релиз текущей версии с GitHub, затем восстановить файлы приложения? Базы KDBX, настройки и учётные данные сохранятся. Приложение перезапустится."),
            UiLang.T("Восстановить"))) return;
        _repairRunning=true;
        RefreshUpdateStatus();
        try
        {
            await _app.CheckForRepairAsync();
            if(updates.IsReinstallReady)
                await _app.InstallReadyUpdateAsync(true,userConfirmed:true);
            else if(updates.IsReady)
            {
                // A newer release appeared after the user's repair confirmation.
                // Request ordinary upgrade confirmation instead of silently installing it.
                await ConfirmInstallUpdateAsync();
            }
            else if(updates.Snapshot.Status=="current")
                await MessageDialog.Info(this,UiLang.T("Восстановление приложения"),
                    UiLang.T("Текущий релиз недоступен на GitHub. Восстановление не выполнялось."));
            // Error details remain visible in the Updates status area.
        }
        catch(Exception ex){ShowProblem(UiLang.T("Восстановление приложения"),ex.Message);}
        finally
        {
            _repairRunning=false;
            RefreshUpdateStatus();
        }
    }

    private async Task ConfirmInstallUpdateAsync()
    {
        var updates=DesktopPlatform.Current.Updates;
        if(!updates.IsReady)
        {
            await MessageDialog.Info(this,"Обновления",UiLang.T("Нет подготовленного обновления."));
            return;
        }
        var repair=updates.IsReinstallReady;
        var description=UiLang.T(repair
            ? "Завершить текущие операции и восстановить файлы приложения? Приложение перезапустится."
            : "Завершить текущие операции и перезапустить приложение для установки обновления?");
        if(updates.Snapshot.Unverified)
            description += "\n\n" + UiLang.T("GitHub Immutable Releases отключён: подлинность обновления не подтверждена.");
        if(await MessageDialog.AskAction(this,
            UiLang.T(repair?"Восстановление приложения":"Установить обновление"),description,
            UiLang.T(repair?"Восстановить":"Установить обновление")))
            await _app.InstallReadyUpdateAsync(true,userConfirmed:true);
    }
}
