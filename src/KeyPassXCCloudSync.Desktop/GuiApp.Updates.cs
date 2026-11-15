using Avalonia.Threading;

namespace KeyPassXCCloudSync;

public sealed partial class GuiApp
{
    private DispatcherTimer? _updatesTimer;
    private bool _installPending;
    private bool _promptCommandLineRepair;
    private bool _manualRepairCheck;
    private string _lastUpdateNotification = "";
    private string _lastUpdatePrompt = "";
    private IUpdateService UpdateService => DesktopPlatform.Current.Updates;

    private void StartUpdates()
    {
        try
        {
            UpdateService.CleanupCompleted();
            UpdateService.Changed += () => Dispatcher.UIThread.Post(OnUpdateChanged);
            _updatesTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
            _updatesTimer.Tick += (_, _) => _ = UpdateService.CheckAsync(false);
            _updatesTimer.Start();
            // The Windows adapter consumes the one-shot request; shared UI stays OS-neutral.
            var repair=DesktopPlatform.Current.ConsumeReinstallRequest();
            _promptCommandLineRepair=repair;
            _ = UpdateService.CheckAsync(repair,reinstall:repair);
        }
        catch (Exception ex) { Shell?.ShowProblem("Обновления", ex.Message); }
    }

    private void OnUpdateChanged()
    {
        var state = UpdateService.Snapshot;
        if (_trayUpdate is not null)
        {
            _trayUpdate.IsEnabled = UpdateService.IsReady;
            _trayUpdate.Header = UiLang.T(UpdateService.IsReady
                ? (UpdateService.IsReinstallReady?"Восстановить…":"Установить обновление…")
                : "Обновлений нет");
        }
        Shell?.RefreshUpdateStatus();
        if (state.Status=="ready" && !_manualRepairCheck &&
            (UpdateService.IsReinstallReady ? _promptCommandLineRepair :
                UpdateService.Preferences.Installation=="ask") &&
            state.Version!=_lastUpdatePrompt && Shell is {IsVisible:true} shell)
        {
            _lastUpdatePrompt=state.Version;
            _promptCommandLineRepair=false;
            _=OfferUpdateRestartAsync(shell);
        }
        if (state.Status is not ("available" or "ready") ||
            state.Version == _lastUpdateNotification) return;
        _lastUpdateNotification = state.Version;
        if (Shell is not null)
        {
            try
            {
                Notifications.Send(Shell,
                    UiLang.T(state.Reinstall?"Доступно восстановление":"Доступно обновление")+" "+state.Version,
                    state.Unverified ? UiLang.T("GitHub Immutable Releases отключён: подлинность обновления не подтверждена.") :
                    UiLang.T("Обновление проверено и готовится к установке."));
            }
            catch (Exception) { /* Notification failure must not affect synchronization. */ }
        }
    }

    private async Task OfferUpdateRestartAsync(ShellWindow shell)
    {
        try
        {
            var repair=UpdateService.IsReinstallReady;
            var message=UiLang.T(repair
                ? "Завершить текущие операции и восстановить файлы приложения? Приложение перезапустится."
                : "Завершить текущие операции и перезапустить приложение для установки обновления?");
            if(UpdateService.Snapshot.Unverified)
                message+="\n\n"+UiLang.T("GitHub Immutable Releases отключён: подлинность обновления не подтверждена.");
            if(await MessageDialog.AskAction(shell,
                UiLang.T(repair?"Восстановление приложения":"Установить обновление"),message,
                UiLang.T(repair?"Восстановить":"Установить обновление")))
                await InstallReadyUpdateAsync(true,userConfirmed:true);
        }
        catch(Exception ex){shell.ShowProblem("Обновления",ex.Message);}
    }

    internal async Task ConfirmTrayUpdateAsync()
    {
        if(Shell is not { } shell || !UpdateService.IsReady)return;
        OpenWindow();
        await OfferUpdateRestartAsync(shell);
    }

    // An interactive Repair owns its confirmation. Do not show a second dialog.
    internal async Task CheckForRepairAsync()
    {
        _manualRepairCheck=true;
        _promptCommandLineRepair=false;
        try { await UpdateService.CheckAsync(true,reinstall:true); }
        finally
        {
            if(UpdateService.Snapshot.Status=="ready")
                _lastUpdatePrompt=UpdateService.Snapshot.Version;
            _manualRepairCheck=false;
        }
    }

    internal Task CheckUpdatesAsync(bool manual, bool reinstall = false) =>
        UpdateService.CheckAsync(manual,reinstall:reinstall);

    internal async Task InstallReadyUpdateAsync(bool restart,bool userConfirmed=false)
    {
        if (_installPending || _allowExit || !UpdateService.IsReady) return;
        if (UpdateService.IsReinstallReady && !userConfirmed)
        {
            Shell?.ShowProblem("Обновления",
                UiLang.T("Для восстановления требуется подтверждение пользователя."));
            return;
        }
        if (Shell?.CanInstallUpdate != true)
        {
            Shell?.ShowProblem("Обновления", UiLang.T("Дождитесь завершения диагностики или сохранения настроек."));
            return;
        }
        _installPending = true;
        try
        {
            UpdateService.ValidateInstallation();
            if (Engine is not null && !await Engine.StopForUpdateAsync(TimeSpan.FromMinutes(3)))
                throw new TimeoutException("Background operations did not finish safely. Update aborted.");
            var background = Shell is null || !Shell.IsVisible || Shell.WindowState == Avalonia.Controls.WindowState.Minimized;
            UpdateService.LaunchInstaller(restart, ConfigPath, background,
                confirmedReinstall:userConfirmed);
            CompleteExit();
        }
        catch (Exception ex)
        {
            _installPending = false;
            Shell?.ShowProblem("Обновление не установлено", ex.Message);
        }
    }
}
