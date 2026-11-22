using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KeyPassXCCloudSync;

internal sealed partial class ShellWindow
{
    private TextBlock? _updateStatus, _protonUpdateStatus;
    private Button? _checkUpdatesButton, _installUpdateButton, _repairUpdateButton;
    private Button? _checkProtonButton, _installProtonButton;
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
        // Application update actions live on About; only policies remain here.
        root.Children.Add(Text(UiLang.T("Proton Drive CLI"),14,true));
        var protonAutomatic=new CheckBox
        {
            Content=UiLang.T("Автоматически проверять обновления Proton CLI"),
            IsChecked=updates.Preferences.CheckProtonCliAutomatically
        };
        protonAutomatic.IsCheckedChanged+=(_,_)=>
        {
            var enabled=protonAutomatic.IsChecked==true;
            updates.SavePreferences(updates.Preferences with {CheckProtonCliAutomatically=enabled});
            if(enabled)_=_app.CheckProtonCliAsync(false);
        };
        root.Children.Add(protonAutomatic);
        _protonUpdateStatus=Text("",12,false,Secondary);
        root.Children.Add(_protonUpdateStatus);
        _checkProtonButton=Action("Проверить CLI",()=>_= _app.CheckProtonCliAsync(true));
        _installProtonButton=Action("Обновить CLI",()=>_= _app.InstallProtonCliAsync());
        root.Children.Add(Row(_checkProtonButton,_installProtonButton));
        RefreshUpdateStatus();
        return root;
    }

    internal void RefreshUpdateStatus()
    {
        // Settings only owns Proton controls; About owns app-update controls.
        // Both statuses must refresh even when the other page is not visible.
        var proton=ProtonCliUpdater.Status;
        if(_protonUpdateStatus is not null)
        {
            var caption=proton.State switch
            {
                "missing"=>"Proton Drive CLI не найден",
                "checking"=>"Проверка Proton CLI…",
                "current"=>"Proton CLI: актуальная версия",
                "available"=>"Доступно обновление Proton Drive CLI",
                "error"=>"Ошибка проверки Proton CLI",
                _=>"Proton CLI: обновления не проверялись"
            };
            _protonUpdateStatus.Text=UiLang.T(caption)+
                (proton.Current.Length>0?" · "+proton.Current:"")+
                (proton.Latest.Length>0 && proton.State=="available"?" → "+proton.Latest:"")+
                (proton.State=="error"?"\n"+proton.Error:"");
        }
        if(_checkProtonButton is not null)_checkProtonButton.IsEnabled=proton.State!="checking";
        if(_installProtonButton is not null)
        {
            _installProtonButton.Content=UiKit.IconLabel(proton.State=="missing"?"Скачать CLI":"Обновить CLI");
            _installProtonButton.IsEnabled=proton.State is "available" or "missing";
        }
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
        if(_updateStatus is not null)
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

    // First full-width About strip: current status left, actions right.
    // Installation and repair use the exact existing confirmation/safety flow.
    private Control BuildApplicationUpdates()
    {
        var layout=new Grid
        {
            Name="AboutUpdateLayout",
            ColumnDefinitions=new ColumnDefinitions("*,Auto"),
            RowDefinitions=new RowDefinitions("Auto,Auto"),
            ColumnSpacing=12,RowSpacing=3
        };
        layout.Children.Add(Text(UiLang.T("Обновления приложения"),14,true));
        _updateStatus=Text("",11,false,Secondary);
        _updateStatus.Name="AboutUpdateStatus";
        _updateStatus.TextWrapping=Avalonia.Media.TextWrapping.Wrap;
        _updateStatus.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetRow(_updateStatus,1);
        layout.Children.Add(_updateStatus);

        _checkUpdatesButton=Action("Проверить обновления",()=>_=CheckForUpdateAsync(),icon:"refresh");
        _checkUpdatesButton.Name="AboutUpdateCheckButton";
        _installUpdateButton=Action("Установить сейчас",()=>_=ConfirmInstallUpdateAsync(),icon:"check");
        _installUpdateButton.Name="AboutUpdateInstallButton";
        _repairUpdateButton=Action("Восстановить…",()=>_=RestoreCurrentVersionAsync(),icon:"history");
        _repairUpdateButton.Name="AboutUpdateRepairButton";
        var actions=new WrapPanel
        {
            Name="AboutUpdateActions",Orientation=Orientation.Horizontal,
            ItemSpacing=7,LineSpacing=5,
            HorizontalAlignment=HorizontalAlignment.Right,
            VerticalAlignment=VerticalAlignment.Center
        };
        actions.Children.Add(_checkUpdatesButton);
        actions.Children.Add(_installUpdateButton);
        actions.Children.Add(_repairUpdateButton);
        Grid.SetColumn(actions,1);
        Grid.SetRowSpan(actions,2);
        layout.Children.Add(actions);
        bool? previousCompact=null;
        layout.SizeChanged+=(_,e)=>
        {
            var compact=e.NewSize.Width<680;
            if(previousCompact==compact)return;
            previousCompact=compact;
            layout.ColumnDefinitions=new ColumnDefinitions(compact?"*":"*,Auto");
            layout.RowDefinitions=new RowDefinitions(compact?"Auto,Auto,Auto":"Auto,Auto");
            Grid.SetColumn(actions,compact?0:1);
            Grid.SetRow(actions,compact?2:0);
            Grid.SetRowSpan(actions,compact?1:2);
            actions.HorizontalAlignment=compact?HorizontalAlignment.Left:HorizontalAlignment.Right;
        };
        RefreshUpdateStatus();
        var card=AboutCard(layout);
        card.Name="AboutUpdatesCard";
        return card;
    }
}
