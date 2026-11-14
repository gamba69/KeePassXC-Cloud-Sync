using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KeyPassXCCloudSync;

internal sealed partial class ShellWindow
{
    private TextBlock? _updateStatus;
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
        RefreshUpdateStatus();
        var buttons = Row(
            Action("Проверить обновления",()=>_=CheckForUpdateAsync()),
            Action("Установить сейчас",()=>_=ConfirmInstallUpdateAsync()));
        root.Children.Add(buttons);
        return root;
    }

    internal void RefreshUpdateStatus()
    {
        if (_updateStatus is null) return;
        var state=DesktopPlatform.Current.Updates.Snapshot;
        var key=state.Status switch
        {
            "checking"=>"Проверка обновлений…",
            "available"=>"Доступна новая версия",
            "ready"=>"Обновление готово",
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

    private async Task ConfirmInstallUpdateAsync()
    {
        var updates=DesktopPlatform.Current.Updates;
        if(!updates.IsReady)
        {
            await MessageDialog.Info(this,"Обновления",UiLang.T("Нет подготовленного обновления."));
            return;
        }
        var description=UiLang.T("Завершить текущие операции и перезапустить приложение для установки обновления?");
        if(updates.Snapshot.Unverified)
            description += "\n\n" + UiLang.T("GitHub Immutable Releases отключён: подлинность обновления не подтверждена.");
        if(await MessageDialog.AskAction(this,UiLang.T("Установить обновление"),description,UiLang.T("Установить обновление")))
            await _app.InstallReadyUpdateAsync(true);
    }
}
