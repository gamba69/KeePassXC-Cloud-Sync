using Avalonia.Threading;

namespace KeyPassXCCloudSync;

public sealed partial class GuiApp
{
    private DispatcherTimer? _updatesTimer;
    private bool _installPending;
    private bool _manualRepairCheck;
    private bool _updateDialogActive;
    private string _lastUpdateNotification = "";
    private string _lastUpdatePrompt = "";
    private string _lastProtonPrompt = "";
    private bool _protonPromptActive;
    private IUpdateService UpdateService => DesktopPlatform.Current.Updates;

    private void StartUpdates()
    {
        try
        {
            var completion=UpdateService.ReadOperationResult();
            // Read before cleanup: an 8.0.x helper left only completed.txt.
            UpdateService.CleanupCompleted();
            if(completion is not null && (completion.Status!="installed" ||
                string.Equals(completion.ToVersion,BuildInfo.Version,StringComparison.Ordinal)) &&
                Shell is { } initialShell)
            {
                bool queued=false;
                initialShell.Opened+=(_,_)=>
                {
                    if(queued)return;
                    queued=true;
                    Dispatcher.UIThread.Post(async ()=>await ShowUpdateResultAsync(completion));
                };
            }
            UpdateService.Changed += () => Dispatcher.UIThread.Post(OnUpdateChanged);
            ProtonCliUpdater.Changed += () => Dispatcher.UIThread.Post(OnProtonCliChanged);
            _updatesTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
            _updatesTimer.Tick += (_, _) =>
            {
                _ = UpdateService.CheckAsync(false);
                if(UpdateService.Preferences.CheckProtonCliAutomatically)
                    _ = ProtonCliUpdater.CheckAsync(ConfigPath);
            };
            _updatesTimer.Start();
            if(UpdateService.Preferences.CheckProtonCliAutomatically)
                _ = ProtonCliUpdater.CheckAsync(ConfigPath);
            // A command-line repair is interactive before any download starts.
            var repair=DesktopPlatform.Current.ConsumeReinstallRequest();
            if(repair && Shell is { } repairShell)
            {
                bool queued=false;
                repairShell.Opened+=(_,_)=>
                {
                    if(queued)return;
                    queued=true;
                    Dispatcher.UIThread.Post(async ()=>await StartCommandLineRepairAsync());
                };
            }
            else _ = UpdateService.CheckAsync(false);
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
        if (state.Status=="ready" && !_manualRepairCheck && !_updateDialogActive &&
            !UpdateService.IsReinstallReady && UpdateService.Preferences.Installation=="ask" &&
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
                Notifications.Send(Shell,
                    UiLang.T(state.Reinstall?"Доступно восстановление":"Доступно обновление")+" "+state.Version,
                    state.Unverified ? UiLang.T("GitHub Immutable Releases отключён: подлинность обновления не подтверждена.") :
                    UiLang.T("Обновление проверено и готовится к установке."));
            }
            catch (Exception) { /* Notification failure must not affect synchronization. */ }
        }
    }

    private void OnProtonCliChanged()
    {
        Shell?.RefreshUpdateStatus();
        var state=ProtonCliUpdater.Status;
        if(state.State!="available" || state.Latest==_lastProtonPrompt ||
            _protonPromptActive)return;
        _lastProtonPrompt=state.Latest;
        if(Shell is {IsVisible:true} shell)
            _=OfferProtonCliUpdateAsync(shell);
        else if(Shell is not null)
            Notifications.Send(Shell,UiLang.T("Доступно обновление Proton Drive CLI"),
                state.Current+" → "+state.Latest);
    }

    private bool ProtonCloudsIdle()
    {
        var engine=Engine;
        if(engine?.Current is not { } cfg)return true;
        return cfg.Clouds.Where(c=>c.Provider=="ProtonDrive")
            .All(c=>engine.Activity.Get(c.CloudId)?.Active!=true);
    }

    internal async Task CheckProtonCliAsync(bool manual)
    {
        await ProtonCliUpdater.CheckAsync(ConfigPath);
        if(!manual || Shell is not { } shell)return;
        var state=ProtonCliUpdater.Status;
        if(state.State=="available")await OfferProtonCliUpdateAsync(shell);
        else if(state.State=="missing")await MessageDialog.Info(shell,
            UiLang.T("Proton Drive CLI"),UiLang.T("Proton Drive CLI не найден"));
        else if(state.State=="error")await MessageDialog.Info(shell,
            UiLang.T("Proton Drive CLI"),state.Error);
    }

    private async Task OfferProtonCliUpdateAsync(ShellWindow shell)
    {
        if(_protonPromptActive)return;
        _protonPromptActive=true;
        try
        {
            var state=ProtonCliUpdater.Status;
            if(state.State!="available")return;
            var message=UiLang.T("Скачать и проверить новую версию Proton CLI, затем заменить исполняемый файл после завершения всех синхронизаций Proton? Перезапуск приложения не требуется.");
            message+="\n\n"+state.Path;
            if(await MessageDialog.AskAction(shell,UiLang.T("Обновление Proton Drive CLI"),
                message,UiLang.T("Обновить CLI")))
                await InstallProtonCliAsync();
        }
        catch(Exception ex){shell.ShowProblem(UiLang.T("Proton Drive CLI"),ex.Message);}
        finally{_protonPromptActive=false;}
    }

    internal async Task InstallProtonCliAsync()
    {
        if(Shell is not { } shell)return;
        var state=ProtonCliUpdater.Status;
        if(state.State is not ("available" or "missing"))return;
        var dialog=new UpdateOperationWindow("proton",state.Current,state.Latest);
        await dialog.RunPreparationAsync(shell,async token=>
        {
            await ProtonCliUpdater.InstallAsync(ConfigPath,false,ProtonCloudsIdle,
                new Progress<UpdateOperationProgress>(dialog.SetProgress),token);
            return true;
        });
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

    private async Task StartCommandLineRepairAsync()
    {
        if(Shell is not { } shell)return;
        try
        {
            OpenWindow();
            if(!await MessageDialog.AskAction(shell,UiLang.T("Восстановление приложения"),
                UiLang.T("Скачать и проверить официальный релиз текущей версии с GitHub, затем восстановить файлы приложения? Базы KDBX, настройки и учётные данные сохранятся. Приложение перезапустится."),
                UiLang.T("Восстановить")))return;
            await CheckForRepairAsync();
            if(UpdateService.IsReinstallReady)await InstallReadyUpdateAsync(true,userConfirmed:true);
            else if(UpdateService.IsReady)await OfferUpdateRestartAsync(shell);
        }
        catch(Exception ex){shell.ShowProblem(UiLang.T("Восстановление приложения"),ex.Message);}
    }

    private async Task ShowUpdateResultAsync(UpdateOperationResult result)
    {
        if(Shell is not { } shell)return;
        try
        {
            // The helper can still own the foreground while it starts this EXE.
            // Unlike persistent Topmost, this temporary activation restores the
            // user's focus policy after bringing the result modal forward.
            await Task.Delay(450);
            OpenWindow();
            if(!shell.IsVisible)return;
            var wasTopmost=shell.Topmost;
            try
            {
                shell.Topmost=true;
                shell.Activate();
                await Task.Delay(150);
            }
            finally {shell.Topmost=wasTopmost;}
            shell.Activate();
            await UpdateOperationWindow.ShowResultAsync(shell,result);
            UpdateService.AcknowledgeOperationResult();
        }
        catch(Exception ex)
        {
            // Do not acknowledge a result that the user could not read.
            shell.ShowProblem(UiLang.T("Обновления"),ex.Message);
        }
    }

    // Interactive checking, including the download, uses an Avalonia modal.
    internal async Task CheckUpdatesAsync(bool manual,bool reinstall=false)
    {
        if(!manual || Shell is not { } shell)
        {
            await UpdateService.CheckAsync(manual,reinstall:reinstall);
            return;
        }
        if(_updateDialogActive)return;
        _updateDialogActive=true;
        try
        {
            var operation=new UpdateOperationWindow(reinstall?"repair":"update",
                BuildInfo.Version,BuildInfo.Version);
            await operation.RunPreparationAsync(shell,async token=>
            {
                IProgress<UpdateOperationProgress> feedback=
                    new Progress<UpdateOperationProgress>(operation.SetProgress);
                await UpdateService.CheckAsync(true,token,reinstall,feedback);
                if(UpdateService.Snapshot.Status=="error")
                    throw new InvalidOperationException(UpdateService.Snapshot.Detail);
                return true;
            });
        }
        finally {_updateDialogActive=false;}
        // The service may report a ready update while this modal is open.
        // Show a restart prompt only after the preparation modal has closed.
        var state=UpdateService.Snapshot;
        if(!reinstall && state.Status=="ready" && !UpdateService.IsReinstallReady &&
            UpdateService.Preferences.Installation=="ask" &&
            state.Version!=_lastUpdatePrompt && shell.IsVisible)
        {
            _lastUpdatePrompt=state.Version;
            await OfferUpdateRestartAsync(shell);
        }
    }

    // An interactive Repair owns its confirmation. Do not show a second dialog.
    internal async Task CheckForRepairAsync()
    {
        _manualRepairCheck=true;
        try {await CheckUpdatesAsync(true,reinstall:true);}
        finally
        {
            if(UpdateService.Snapshot.Status=="ready")
                _lastUpdatePrompt=UpdateService.Snapshot.Version;
            _manualRepairCheck=false;
        }
    }

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
            async Task<bool> PrepareAndLaunchAsync(UpdateOperationWindow? window,CancellationToken token)
            {
                window?.SetProgress(new("verifying"));
                await Task.Run(UpdateService.ValidateInstallation,token);
                token.ThrowIfCancellationRequested();
                // Once quiescence starts, cancellation would leave the sync engine stopped.
                window?.DisableCancellation();
                window?.SetProgress(new("stopping"));
                if (Engine is not null && !await Engine.StopForUpdateAsync(TimeSpan.FromMinutes(3)))
                    throw new TimeoutException("Background operations did not finish safely. Update aborted.");
                window?.SetProgress(new("handoff"));
                var background = Shell is null || !Shell.IsVisible ||
                    Shell.WindowState == Avalonia.Controls.WindowState.Minimized;
                UpdateService.LaunchInstaller(restart,ConfigPath,background,
                    confirmedReinstall:userConfirmed);
                return true;
            }
            if(restart && Shell is { IsVisible:true } owner)
            {
                var dialog=new UpdateOperationWindow(UpdateService.IsReinstallReady?"repair":"update",
                    BuildInfo.Version,UpdateService.Snapshot.Version.TrimStart('v'));
                if(!await dialog.RunPreparationAsync(owner,token=>PrepareAndLaunchAsync(dialog,token)))
                {
                    _installPending=false;
                    return;
                }
            }
            else await PrepareAndLaunchAsync(null,CancellationToken.None);
            CompleteExit();
        }
        catch (Exception ex)
        {
            _installPending = false;
            Shell?.ShowProblem("Обновление не установлено", ex.Message);
        }
    }
}
