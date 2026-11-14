using Avalonia.Threading;

namespace KeyPassXCCloudSync;

public sealed partial class GuiApp
{
    private DispatcherTimer? _updatesTimer;
    private bool _installPending;
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
            _ = UpdateService.CheckAsync(false);
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
                ? "Установить обновление…" : "Обновлений нет");
        }
        Shell?.RefreshUpdateStatus();
        if (state.Status=="ready" && UpdateService.Preferences.Installation=="ask" &&
            state.Version!=_lastUpdatePrompt && Shell is {IsVisible:true} shell)
        {
            _lastUpdatePrompt=state.Version;
            _=OfferUpdateRestartAsync(shell);
        }
        if (state.Status is not ("available" or "ready") ||
            state.Version == _lastUpdateNotification) return;
        _lastUpdateNotification = state.Version;
        if (Shell is not null)
        {
            try
            {
                Notifications.Send(Shell, UiLang.T("Доступно обновление") + " " + state.Version,
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
            var message=UiLang.T("Завершить текущие операции и перезапустить приложение для установки обновления?");
            if(UpdateService.Snapshot.Unverified)
                message+="\n\n"+UiLang.T("GitHub Immutable Releases отключён: подлинность обновления не подтверждена.");
            if(await MessageDialog.AskAction(shell,UiLang.T("Установить обновление"),message,UiLang.T("Установить обновление")))
                await InstallReadyUpdateAsync(true);
        }
        catch(Exception ex){shell.ShowProblem("Обновления",ex.Message);}
    }

    internal Task CheckUpdatesAsync(bool manual) => UpdateService.CheckAsync(manual);

    internal async Task InstallReadyUpdateAsync(bool restart)
    {
        if (_installPending || _allowExit || !UpdateService.IsReady) return;
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
            UpdateService.LaunchInstaller(restart, ConfigPath, background);
            CompleteExit();
        }
        catch (Exception ex)
        {
            _installPending = false;
            Shell?.ShowProblem("Обновление не установлено", ex.Message);
        }
    }
}
