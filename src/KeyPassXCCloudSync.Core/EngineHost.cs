using System.Security.Cryptography;
using System.Text;

namespace KeyPassXCCloudSync;

// The same operation semaphore protects GUI diagnostics, OAuth, Merge and the worker.
// A platform database lock prevents concurrent writers for the same KDBX.
internal sealed class EngineHost : IDisposable
{
    private readonly string _configPath;
    private readonly CancellationTokenSource _stop=new();
    private readonly SemaphoreSlim _wake=new(0,1);
    internal readonly SemaphoreSlim Gate=new(1,1);
    private AppOptions? _cfg;
    private IDisposable? _watcher;
    private IDisposable? _networkMonitor;
    private IDatabaseReservation? _databaseMutex;
    private string _mutexIdentity="";
    private bool _disposed;
    private int _forceNext=1;
    private int _forceTopology=1;
    private readonly CloudRequestMailbox _cloudRequests=new();
    private IndependentCloudScheduler? _cloudScheduler;
    private Task? _statisticsJob;
    private CancellationTokenSource? _statisticsCancel;
    internal CloudActivityTracker Activity {get;}=new();
    private readonly object _progressLock=new();
    private EngineProgress _progress=new("WAIT", "Ожидаю настройки", null);
    internal event Action? ProgressUpdated;
    internal event Action<AppState>? Updated;
    internal event Action<string,string>? ErrorReported;
    internal event Action? SharedConfigurationReloaded;

    // Only transient UI status: never persisted as synchronization metadata.
    // Updates arrive from the worker; consumers marshal notifications to the UI thread.
    internal sealed record EngineProgress(string Phase,string Detail,DateTimeOffset? DeadlineUtc);
    internal EngineProgress Progress
    {
        get {lock(_progressLock)return _progress;}
    }
    internal string ProgressLine()
    {
        var progress=Progress;
        if(progress.DeadlineUtc is not { } deadline)return progress.Detail;
        var remaining=deadline-DateTimeOffset.UtcNow;
        var seconds=Math.Max(0,(long)Math.Ceiling(remaining.TotalSeconds));
        var hours=seconds/3600;
        var time=hours>0
            ? $"{hours:00}:{(seconds/60)%60:00}:{seconds%60:00}"
            : $"{seconds/60:00}:{seconds%60:00}";
        return progress.Detail+" · осталось "+time;
    }
    private void ReportProgress(string phase,string detail,DateTimeOffset? deadlineUtc=null)
    {
        lock(_progressLock)_progress=new EngineProgress(phase,detail,deadlineUtc);
        ProgressUpdated?.Invoke();
    }
    internal AppOptions? Current=>_cfg;
    internal bool SyncEnabled=>_cfg?.SyncEnabled==true;

    internal EngineHost(string path){_configPath=path;}
    internal void Start()
    {
        if(File.Exists(_configPath))
        {
            try{ApplyConfig(AppOptions.Load(_configPath));}
            catch(Exception ex){ShowError("Не удалось открыть конфигурацию",ex.Message);}
        }
        _networkMonitor=PlatformServices.Current.Changes.WatchNetwork(Wake);
        _=Task.Run(RunWithStartupBackupAsync);
    }
    // Startup snapshots must finish before the very first synchronization cycle.
    // Run on a worker thread: a large database must never freeze the Avalonia UI.
    private async Task RunWithStartupBackupAsync()
    {
        try
        {
            await Gate.WaitAsync(_stop.Token);
            try
            {
                var cfg=_cfg;
                if(cfg is not null && cfg.BackupOnStartup)
                {
                    try
                    {
                        var created=StartupBackupService.CreateIfPresent(cfg.LocalFile,
                            cfg.DataDirectory,cfg.StartupBackupKeep);
                        if(created is not null)
                        {
                            try { new SyncLog(cfg).Info("Startup KDBX backup created: "+created); }
                            catch(IOException){} catch(UnauthorizedAccessException){}
                        }
                    }
                    catch(Exception ex)
                    {
                        try { new SyncLog(cfg).Warn("Startup backup failed: "+ex.Message); }
                        catch(IOException){} catch(UnauthorizedAccessException){}
                        ShowError("Резервная копия при запуске — предупреждение",ex.Message+"\n\n"+
                            "Синхронизация продолжит работу. Проверьте каталог локальных резервных копий.");
                    }
                }
            }
            finally { Gate.Release(); }
        }
        catch(OperationCanceledException) when(_stop.IsCancellationRequested) { return; }
        await LoopAsync();
    }
    private void ShowError(string title,string detail)=>ErrorReported?.Invoke(title,detail);
    private static string Identity(string localFile)=>Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(PlatformServices.Current.Paths.PathComparison==StringComparison.OrdinalIgnoreCase
            ? Path.GetFullPath(localFile).ToUpperInvariant() : Path.GetFullPath(localFile))))[..20];

    // Reservations are owned by the platform lock service, not the UI thread.
    // A failed Save disposes the temporary lease without changing the active KDBX.
    internal IDatabaseReservation? ReserveFile(string file)
    {
        var identity=Identity(file);
        if(identity==_mutexIdentity)return null;
        // A stable per-database mutex prevents concurrent writers.
        return PlatformServices.Current.DatabaseLocks.Acquire(file);
    }
    internal void ReleaseReservation(IDatabaseReservation? reservation)
    {
        if(reservation is null)return;
        reservation.Dispose();
    }
    internal void ApplyConfig(AppOptions cfg,IDatabaseReservation? reservation=null)
    {
        var identity=Identity(cfg.LocalFile);
        if(identity!=_mutexIdentity)
        {
            reservation??=ReserveFile(cfg.LocalFile);
            if(reservation is null)throw new InvalidOperationException("Missing database lock");
            var old=_databaseMutex;
            _databaseMutex=reservation;_mutexIdentity=identity;
            old?.Dispose();
        }
        else if(reservation is not null)ReleaseReservation(reservation);
        _cloudScheduler?.Dispose();
        _cloudScheduler=null;
        try{_statisticsCancel?.Cancel();}catch(ObjectDisposedException){}
        _cfg=cfg;
        _cloudRequests.Clear();
        Activity.Clear();
        Interlocked.Exchange(ref _forceTopology,1);
        _watcher?.Dispose();_watcher=null;
        var dir=Path.GetDirectoryName(cfg.LocalFile)!;
        Directory.CreateDirectory(dir);
        _watcher=PlatformServices.Current.Changes.WatchFile(cfg.LocalFile,() =>
        {
            if(SyncEnabled)Wake();
        });
        ReportProgress(cfg.SyncEnabled?"READY":"PAUSED",cfg.SyncEnabled
            ?"Настройки изменены · проверяю доступность реплик…"
            :"KDBX на паузе · топология облаков опрашивается");
        Wake();
    }
    private void SignalWake()
    {
        if(_disposed)return;
        try{if(_wake.CurrentCount==0)_wake.Release();}
        catch(ObjectDisposedException){}
    }
    internal void Wake(){Interlocked.Exchange(ref _forceNext,1);SignalWake();}
    // An explicit cloud request never silently forces an all-provider scan.
    // A discovered change may still require the coordinator to check peers.
    internal bool RequestCloudScan(string cloudId)
    {
        var cfg=_cfg;
        if(_disposed || cfg?.SyncEnabled!=true ||
           !cfg.Clouds.Any(c=>c.CloudId==cloudId))return false;
        // An additional click while a cloud is active queues at most one
        // follow-up scan, rather than silently ignoring the user's request.
        // Requests from an ongoing cycle are not included in its captured set.
        Activity.Requested(cloudId);
        if(_cloudScheduler?.Request(cloudId)==true)return true;
        _cloudRequests.Enqueue(cloudId);
        SignalWake();
        return true;
    }
    private IReadOnlySet<string> TakeCloudRequests()=>_cloudRequests.Drain();
    private bool HasPendingWork => _cloudRequests.HasPending ||
        Volatile.Read(ref _forceNext)!=0 || Volatile.Read(ref _forceTopology)!=0;

    // Each cloud independently prepares its remote observation without holding
    // the shared state semaphore. The coordinator acquires Gate only to snapshot
    // local metadata and to reconcile the completed observation (which may
    // trigger a safety sweep if the remote vault has changed).
    private void EnsureCloudScheduler(AppOptions cfg)
    {
        if(_cloudScheduler is not null || !cfg.SyncEnabled || _disposed)return;
        var byId=cfg.Clouds.ToDictionary(c=>c.CloudId,StringComparer.Ordinal);
        DateTimeOffset? NextDue(string id)
        {
            if(!ReferenceEquals(_cfg,cfg))return DateTimeOffset.UtcNow.AddMinutes(1);
            var state=SyncStorage.LoadState(cfg.StateFile);
            return state.NextCloudPollUtc.TryGetValue(id,out var due)
                ?due:DateTimeOffset.UtcNow;
        }
        _cloudScheduler=new IndependentCloudScheduler(byId.Keys,NextDue,
            (id,token)=>ProcessPreparedCloudAsync(cfg,byId[id],token),
            TimeSpan.FromSeconds(Math.Max(3,cfg.RetrySeconds)),_stop.Token,
            (id,ex)=>
            {
                try{new SyncLog(cfg).Warn("Independent scan "+id+": "+ex.Message);}
                catch(IOException){}catch(UnauthorizedAccessException){}
                Activity.End(id,false);
            });
    }

    private async Task ProcessPreparedCloudAsync(AppOptions cfg,CloudOptions cloud,CancellationToken ct)
    {
        Activity.Requested(cloud.CloudId);
        var log=new SyncLog(cfg);
        var preparation=new V2Engine(cfg,log){LiveActivity=Activity};
        AppState snapshot;
        await Gate.WaitAsync(ct);
        try
        {
            if(!ReferenceEquals(_cfg,cfg) || !cfg.SyncEnabled)return;
            snapshot=SyncStorage.LoadState(cfg.StateFile);
            if(snapshot.TopologyDisabled)return;
            preparation.PrepareVersionSnapshot(snapshot);
        }
        finally {Gate.Release();}
        // No shared Gate is held during remote I/O, including slow CLI calls.
        using var prepared=await preparation.PrepareCloudScanAsync(cloud,snapshot,
            Guid.NewGuid().ToString("N"),ct);
        await Gate.WaitAsync(ct);
        try
        {
            if(!ReferenceEquals(_cfg,cfg) || !cfg.SyncEnabled)return;
            var reconcile=new V2Engine(cfg,log,
                onActivity:detail=>ReportProgress("SYNC",detail+"…"),
                onProgress:detail=>ReportProgress("CHECK",detail),
                onScanCommitted:intermediate=>Updated?.Invoke(intermediate))
                {LiveActivity=Activity};
            await reconcile.RunPreparedAsync(ct,false,
                new HashSet<string>(StringComparer.Ordinal){cloud.CloudId},
                new Dictionary<string,V2Engine.ScanOutcome>(StringComparer.Ordinal)
                    {[cloud.CloudId]=prepared});
            var state=SyncStorage.LoadState(cfg.StateFile);
            if(state.TopologyRefreshNeeded)
            {
                Interlocked.Exchange(ref _forceTopology,1);
                SignalWake();
            }
            try{UsageStatistics.Record(cfg,state);}
            catch(Exception ex){log.Warn("Usage counter error: "+ex.Message);}
            // A completed independent scan must also retire its global
            // progress line. Otherwise the headline can say SYNCED while the
            // subtitle still claims to be checking an already idle cloud.
            if(!Activity.AnyActive)
                ReportProgress("WAIT",state.Result=="SYNCED"
                    ?"Ожидаю следующей проверки"
                    :state.Result=="CONFLICT"
                        ?"Требуется разрешение конфликта"
                        :"Ожидаю фонового обслуживания");
            Updated?.Invoke(state);
        }
        finally {Gate.Release();}
    }

    // Statistics exchange may take many provider requests. Serialize only
    // the state snapshots and final merge; keep all statistics HTTP/CLI I/O
    // off the global KDBX state semaphore.
    private void StartStatisticsJob(AppOptions cfg,SyncLog log)
    {
        if(_statisticsJob is {IsCompleted:false})return;
        var job=UsageExchange.CreateBackgroundJob(cfg,log);
        if(job is null)return;
        var jobCancel=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _statisticsCancel=jobCancel;
        _statisticsJob=Task.Run(async ()=>
        {
            try
            {
                var apply=await job(jobCancel.Token);
                await Gate.WaitAsync(jobCancel.Token);
                try
                {
                    if(!ReferenceEquals(_cfg,cfg) || _disposed)return;
                    apply();
                    Updated?.Invoke(SyncStorage.LoadState(cfg.StateFile));
                }
                finally {Gate.Release();}
            }
            catch(OperationCanceledException) when(jobCancel.IsCancellationRequested){}
            catch(Exception ex)
            {
                try{log.Warn("Statistics background exchange: "+ex.Message);}
                catch(IOException){}catch(UnauthorizedAccessException){}
            }
            finally{jobCancel.Dispose();}
        });
    }

    private async Task LoopAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                var cfg=_cfg;
                if(cfg is null){ReportProgress("WAIT","Выберите базу KDBX и настройте облака");await _wake.WaitAsync(_stop.Token);continue;}
                await Gate.WaitAsync(_stop.Token);
                int wait;
                try
                {
                    cfg=_cfg;
                    if(cfg is null)continue;
                    var log=new SyncLog(cfg);
                    if(!cfg.SyncEnabled)
                    {
                        ReportProgress("PAUSED","KDBX на паузе · проверяю топологию облаков…");
                        try
                        {
                            var wakeRequested=Interlocked.Exchange(ref _forceNext,0)!=0;
                            var topologyRequested=Interlocked.Exchange(ref _forceTopology,0)!=0;
                            var idle=await TopologyCoordinator.PollPausedAsync(cfg,log,_stop.Token,
                                wakeRequested || topologyRequested);
                            if(idle.Reload)
                                SharedConfigurationReloaded?.Invoke();
                            if(!idle.Left)
                                await UsageExchange.RunAsync(cfg,log,_stop.Token);
                        }
                        catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
                        catch(Exception e)
                        {
                            log.Warn("Paused control plane: "+e.Message);
                            var failure=SyncStorage.LoadState(cfg.StateFile);
                            failure.TopologyError=e.Message;
                            SyncStorage.SaveState(cfg.StateFile,failure);
                        }
                        var idleState=SyncStorage.LoadState(cfg.StateFile);
                        try{UsageStatistics.Record(cfg,idleState);}
                        catch(Exception e){log.Warn("Usage counter error: "+e.Message);}
                        Updated?.Invoke(idleState);
                        var nextCloud=cfg.Clouds.Select(c=>idleState.NextCloudPollUtc.TryGetValue(c.CloudId,out var due)
                            ?Math.Max(1,(int)Math.Ceiling((due-DateTimeOffset.UtcNow).TotalSeconds)):1)
                            .DefaultIfEmpty(60).Min();
                        var usage=UsageStatistics.Load(cfg);
                        var statsSeconds=usage.LastPublishedUtc is { } published
                            ?(int)Math.Ceiling((published.AddMinutes(cfg.StatsPublishMinutes)-
                                DateTimeOffset.UtcNow).TotalSeconds):0;
                        // Overdue/failed stats exchange must not create a one-second retry loop.
                        var nextStats=statsSeconds<=0 ? nextCloud : statsSeconds;
                        wait=idleState.TopologyDisabled?86400:
                            cfg.Clouds.Count==0?60:Math.Min(nextCloud,nextStats);
                        ReportProgress("PAUSED","KDBX на паузе · следующий опрос топологии",
                            DateTimeOffset.UtcNow.AddSeconds(wait));
                    }
                    else
                    {
                    ReportProgress("CHECK","Проверяю локальную базу и состояние облаков…");
                    int signaled=0;
                    void OnActivity(string description)
                    {
                        ReportProgress("SYNC",description+"…");
                        if(Interlocked.Exchange(ref signaled,1)!=0)return;
                        // The callback is only invoked by actual KDBX content operations,
                        // never by a routine status scan or metadata refresh.
                        var snapshot=SyncStorage.LoadState(cfg.StateFile);
                        snapshot.Result="SYNCING";
                        Updated?.Invoke(snapshot);
                    }
                    void OnProgress(string stage)=>ReportProgress("CHECK",stage);
                    try
                    {
                        var forceAll=Interlocked.Exchange(ref _forceNext,0)!=0;
                        var requested=TakeCloudRequests();
                        if(forceAll || requested.Count>0 || _cloudScheduler is null)
                        {
                            var engine=new V2Engine(cfg,log,OnActivity,OnProgress,
                                onScanCommitted:intermediate=>Updated?.Invoke(intermediate))
                                {LiveActivity=Activity};
                            await engine.RunOnceAsync(_stop.Token,forceAll,requested);
                        }
                        EnsureCloudScheduler(cfg);
                        var topology=await TopologyCoordinator.RunAsync(cfg,log,_stop.Token,
                            Interlocked.Exchange(ref _forceTopology,0)!=0);
                        if(topology.Reload)
                            SharedConfigurationReloaded?.Invoke();
                        if(!topology.Left)
                            StartStatisticsJob(cfg,log);
                    }
                    catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
                    catch(SyncSafetyException e){log.Warn("Control plane: "+e.Message);
                        var st=SyncStorage.LoadState(cfg.StateFile);st.TopologyError=e.Message;
                        SyncStorage.SaveState(cfg.StateFile,st);}

                    catch(Exception e){log.Warn("Sync cycle failed: "+e.GetType().Name+": "+e.Message);}
                    var state=SyncStorage.LoadState(cfg.StateFile);
                    try{UsageStatistics.Record(cfg,state);}
                    catch(Exception e){log.Warn("Usage counter error: "+e.Message);}
                    Updated?.Invoke(state);
                    // Per-cloud timers are owned by IndependentCloudScheduler.
                    // This host loop only services control-plane maintenance and
                    // file-change notifications; no global cloud-poll barrier.
                    // Use an idle fallback to catch missed OS file notifications.
                    var localChanged=!File.Exists(cfg.LocalFile);
                    if(!localChanged)
                    {
                        var currentFile=new FileInfo(cfg.LocalFile);
                        localChanged=currentFile.LastWriteTimeUtc.Ticks!=state.LocalFileStamp ||
                            currentFile.Length!=state.LocalFileLength ||
                            state.LastLocalContentCheckUtc is null ||
                            DateTimeOffset.UtcNow-state.LastLocalContentCheckUtc>TimeSpan.FromHours(24);
                    }
                    // Never spin continuously on a missing or damaged KDBX.
                    // The fallback is rate-limited; an OS file-change event
                    // still bypasses the delay through Wake().
                    if(localChanged && (state.LastAttemptUtc is null ||
                       DateTimeOffset.UtcNow-state.LastAttemptUtc>=TimeSpan.FromSeconds(cfg.RetrySeconds)))
                        Interlocked.Exchange(ref _forceNext,1);
                    wait=state.TopologyDisabled?60:Math.Min(60,Math.Max(10,cfg.RetrySeconds));
                    ReportProgress("WAIT",state.Result=="SYNCED"?
                        "Ожидаю следующей проверки":
                        state.Result=="CONFLICT"?"Требуется разрешение конфликта":
                        "Ожидаю фонового обслуживания",DateTimeOffset.UtcNow.AddSeconds(wait));
                    } // KDBX enabled
                }
                finally{Gate.Release();}
                // A manual request can arrive while a slow cloud is still
                // running. Check the mailbox BEFORE sleeping; a previous
                // debounce may have consumed the wake semaphore signal.
                if(HasPendingWork)continue;
                // File events may be frequent; debounce without blocking UI or worker thread.
                var activated=await _wake.WaitAsync(TimeSpan.FromSeconds(wait),_stop.Token);
                if(activated && cfg.SyncEnabled && cfg.DebounceMs>0)
                {
                    ReportProgress("DEBOUNCE","Обнаружено событие · жду стабилизации файла",
                        DateTimeOffset.UtcNow.AddMilliseconds(cfg.DebounceMs));
                    await Task.Delay(cfg.DebounceMs,_stop.Token);
                    while(_wake.Wait(0)){}
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                ReportProgress("ERROR","Ошибка фоновой проверки · повторная попытка",
                    DateTimeOffset.UtcNow.AddSeconds(10));
                ShowError("Ошибка фонового синхронизатора",ex.Message);
                try{await Task.Delay(TimeSpan.FromSeconds(10),_stop.Token);}catch(OperationCanceledException){break;}
            }
        }
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        _stop.Cancel();_cloudScheduler?.Dispose();
        try{_statisticsCancel?.Cancel();}catch(ObjectDisposedException){}
        _watcher?.Dispose();
        _networkMonitor?.Dispose();
        // Exit is called on the UI thread that acquired the mutex.
        _databaseMutex?.Dispose();
        _databaseMutex=null;
        // Do not dispose the semaphore while an interrupted worker may still be unwinding.
    }
}
