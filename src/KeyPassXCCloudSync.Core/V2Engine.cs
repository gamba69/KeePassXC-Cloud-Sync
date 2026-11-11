using System.Text.Json;

namespace KeyPassXCCloudSync;

// Protocol v2. The fast path reads only remote revisions and per-device pointers.
// Immutable KDBX versions are never fetched unless a new head must be installed.
public sealed class V2Engine(AppOptions settings,SyncLog log,Action<string>? onActivity=null,
    Action<string>? onProgress=null,Action<string>? onTransportProgress=null,
    Action<AppState>? onScanCommitted=null)
{
    internal Func<string,string,CancellationToken,Task>? PublicationHookForTests {get;set;}
    internal CloudActivityTracker? LiveActivity {get;set;}
    // Parallel probes stage new immutable versions in separate directories.
    // Only the coordinator may commit them into the shared history/backup tree.
    private string? _probeRoot;
    private string? _fallbackVaultId;
    private string BackupDir=>Path.Combine(_probeRoot??settings.DataDirectory,"backup");
    private string HistoryDir=>Path.Combine(_probeRoot??settings.DataDirectory,"history");
    private string ConflictDir=>Path.Combine(settings.DataDirectory,"conflicts");
    private string PendingFile=>Path.Combine(settings.DataDirectory,"v2-local-install.json");
    private string WorkingKey(CloudOptions c)=>c.RemoteDirectory+"/database.kdbx";
    private static string VersionMeta(CloudOptions c,string id)=>c.RemoteDirectory+"/versions/"+id+".json";
    private static string VersionBody(CloudOptions c,string id)=>c.RemoteDirectory+"/versions/"+id+".kdbx";
    private static string DeviceKey(CloudOptions c,string id)=>c.RemoteDirectory+"/devices/"+id+".json";
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"kps-v2-"+Guid.NewGuid().ToString("N")+".tmp");
    private readonly Dictionary<string,VersionInfo> _versions=new(StringComparer.Ordinal);
    internal Dictionary<string,V2OperationTotals> LastMetrics {get;}=new(StringComparer.OrdinalIgnoreCase);

    internal sealed class Replica(CloudOptions c,IRemoteStore store,V2CloudCursor cursor)
    {
        public CloudOptions Config=c;
        public IRemoteStore Store=store;
        public V2CloudCursor Cursor=cursor;
        public string? Revision;
        public string? WorkingVersion;
        public string? WorkingSha;
        public bool WorkingExists;
        public bool WasChanged;
    }

    internal sealed class ScanOutcome(CloudOptions cloud,V2Engine? engine,Replica? replica,
        AppState? snapshot,string? stage,Exception? error=null,long elapsedMilliseconds=0) : IDisposable
    {
        public CloudOptions Cloud=cloud;
        public V2Engine? Engine=engine;
        public Replica? Replica=replica;
        public AppState? Snapshot=snapshot;
        public string? Stage=stage;
        public Exception? Error=error;
        public long ElapsedMilliseconds=elapsedMilliseconds;
        // Used to refuse a stale background observation after another cycle
        // has already committed a newer scan of this same cloud.
        public DateTimeOffset StartedUtc {get;init;}=DateTimeOffset.UtcNow;
        public void DetachReplica()=>Replica=null;
        public void Dispose()
        {
            Replica?.Store.Dispose();
            if(Stage is not null)
                try{Directory.Delete(Stage,true);}
                catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    internal IRemoteStore CreateStore(CloudOptions cloud)
    {
        if(cloud.Provider is "FileSystemTest" or "FileSystemTestNoCas")
        {
            if(Environment.GetEnvironmentVariable("KPS_ENABLE_TEST_STORE")!="1")
                throw new SyncSafetyException("Test transport disabled");
            return cloud.Provider=="FileSystemTest"
                ?new FileSystemTestStore(new Uri(cloud.BaseUrl).LocalPath)
                :new FileSystemNoCasTestStore(new Uri(cloud.BaseUrl).LocalPath);
        }
        var c=settings.AsTransportConfig(cloud);
        IRemoteStore actual=cloud.Provider switch
        {
            "S3" or "CloudflareR2"=>new S3Store(c,onTransportProgress),
            "WebDAV"=>new WebDavStore(c),
            "GoogleDrive"=>new GoogleDriveStore(c,onTransportProgress),
            "ProtonDrive"=>new ProtonDriveStore(c),
            _=>throw new NotSupportedException(cloud.Provider)
        };
        return actual;
    }
    private IRemoteStore CreateMetered(CloudOptions cloud)
    {
        var original=CreateStore(cloud);
        void Operation(string stage,bool finished,bool success)
        {
            if(finished)LiveActivity?.OperationFinished(cloud.CloudId,stage,success);
            else LiveActivity?.OperationStarted(cloud.CloudId,stage);
        }
        var wrapped=original is IConditionalWriteStore
            ?new V2MeteredCasStore(original,Operation)
            :new V2MeteredStore(original,Operation);
        LastMetrics[cloud.Name]=wrapped.Stats;
        return wrapped;
    }

    internal void PrepareVersionSnapshot(AppState state)=>ReadLocalVersions(state.VaultId);

    internal async Task<ScanOutcome> PrepareCloudScanAsync(CloudOptions cloud, AppState state,
        string commonFirstVaultId, CancellationToken token)
    {
        var scanClock=System.Diagnostics.Stopwatch.StartNew();
        var scanStarted=DateTimeOffset.UtcNow;
        token.ThrowIfCancellationRequested();
        if(!TopologyCoordinator.IsAuthorized(cloud))
            return new ScanOutcome(cloud, null, null, null, null,
                elapsedMilliseconds:scanClock.ElapsedMilliseconds){StartedUtc=scanStarted};
        LiveActivity?.Begin(cloud.CloudId,"Проверка изменений…");
        var worker=new V2Engine(settings,log,onActivity:null,onProgress:null,
            onTransportProgress:onTransportProgress){LiveActivity=LiveActivity};
        var snapshot=JsonSerializer.Deserialize(
            JsonSerializer.Serialize(state,SyncJson.Default.AppState),SyncJson.Default.AppState)!;
        foreach(var (id,version) in _versions)worker._versions.Add(id,version);
        var stage=Path.Combine(settings.DataDirectory,"scan-stage",Guid.NewGuid().ToString("N"));
        worker._probeRoot=stage;
        worker._fallbackVaultId=commonFirstVaultId;
        Directory.CreateDirectory(worker.BackupDir);
        Directory.CreateDirectory(worker.HistoryDir);
        IRemoteStore? transport=null;
        // A provider's sequence of remote requests must have a total budget;
        // individual HTTP timeouts would otherwise accumulate indefinitely.
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(15,cloud.TimeoutSeconds)*3));
        try
        {
            onProgress?.Invoke("Проверяю изменения в "+cloud.Name);
            var cursor=snapshot.V2Cursors.TryGetValue(cloud.CloudId,out var existing)
                ?existing:new V2CloudCursor();
            snapshot.V2Cursors[cloud.CloudId]=cursor;
            transport=worker.CreateMetered(cloud);
            var replica=new Replica(cloud,transport,cursor);
            var before=cursor.WorkingRevision;
            await worker.ScanAsync(replica,snapshot,budget.Token);
            replica.WasChanged=before!=replica.Revision;
            transport=null; // The coordinator owns the store through publication.
            return new ScanOutcome(cloud,worker,replica,snapshot,stage,
                elapsedMilliseconds:scanClock.ElapsedMilliseconds){StartedUtc=scanStarted};
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        {
            try {if(Directory.Exists(stage))Directory.Delete(stage,true);}
            catch(IOException){}catch(UnauthorizedAccessException){}
            throw;
        }
        catch(OperationCanceledException) when(budget.IsCancellationRequested)
        {
            return new ScanOutcome(cloud,worker,null,null,stage,
                new TimeoutException("Cloud scan exceeded its per-cloud time budget"),
                scanClock.ElapsedMilliseconds){StartedUtc=scanStarted};
        }
        catch(Exception e)
        {
            return new ScanOutcome(cloud,worker,null,null,stage,e,scanClock.ElapsedMilliseconds)
                {StartedUtc=scanStarted};
        }
        finally {transport?.Dispose();}
    }

    // CAS providers use a strong server revision. Other transports use listing metadata;
    // an unknown modified timestamp forces conservative re-reading instead of a false idle.
    internal static async Task<string?> RevisionAsync(IRemoteStore store,string key,CancellationToken ct)
    {
        if(store is IConditionalWriteStore cas)
        {
            try{return await cas.ReadTokenAsync(key,ct);}
            catch(SyncSafetyException e) when(e.Message.StartsWith(
                "WebDAV did not provide a strong ETag",StringComparison.Ordinal))
            {
                // WebDAV without a strong ETag remains usable, but CAS is disabled.
            }
        }
        var slash=key.LastIndexOf('/');
        var items=await store.ListAsync(key[..slash],ct);
        var found=items.Where(x=>x.Path==key).ToArray();
        if(found.Length>1)throw new SyncSafetyException("Ambiguous remote filename: "+key);
        if(found.Length==0)return null;
        return found[0].ModifiedUtc==default
            ?"unknown-"+Guid.NewGuid().ToString("N")
            :found[0].ModifiedUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Validate(VersionInfo v,string vault)
    {
        if(!Guid.TryParseExact(v.VersionId,"N",out _) || v.VaultId!=vault ||
           v.Parents.Contains(v.VersionId) || v.Parents.Distinct().Count()!=v.Parents.Length ||
           !Hashes.Valid(v.Sha256) || v.Size<1 ||
           v.BackupName!=v.VersionId+".kdbx")
            throw new SyncSafetyException("Invalid v2 history record");
    }
    private void ReadLocalVersions(string? vault)
    {
        _versions.Clear();
        foreach(var f in Directory.EnumerateFiles(HistoryDir,"*.json"))
        {
            var v=JsonSerializer.Deserialize(File.ReadAllText(f),SyncJson.Default.VersionInfo)
                ??throw new SyncSafetyException("Empty local history record");
            if(vault is not null)Validate(v,vault);
            if(!string.Equals(Path.GetFileNameWithoutExtension(f),v.VersionId,StringComparison.Ordinal) ||
               !_versions.TryAdd(v.VersionId,v))
                throw new SyncSafetyException("Duplicate/invalid local version");
        }
    }
    private static void AtomicCopy(string from,string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        var temporary=to+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.Copy(from,temporary);
            using(var stream=new FileStream(temporary,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                stream.Flush(true);
            File.Move(temporary,to,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private VersionInfo Capture(string file,AppState state,string[] parents,string source)
    {
        var sha=Hashes.Sha256(file);
        var v=SyncStorage.NewVersion(state.VaultId!,state.InstallationId,sha,
            new FileInfo(file).Length,parents,source,"v2");
        v.BackupName=v.VersionId+".kdbx";
        var saved=Path.Combine(BackupDir,v.BackupName);
        File.Copy(file,saved,false);
        SyncStorage.SaveJson(Path.Combine(HistoryDir,v.VersionId+".json"),v,SyncJson.Default.VersionInfo);
        _versions.Add(v.VersionId,v);
        state.LastChangeUtc=DateTimeOffset.UtcNow;
        onActivity?.Invoke("Новая версия: "+source);
        return v;
    }
    private static void Stamp(AppState state,string path)
    {
        if(!File.Exists(path)){state.LocalFileStamp=0;state.LocalFileLength=0;return;}
        var i=new FileInfo(path);
        state.LocalFileStamp=i.LastWriteTimeUtc.Ticks;state.LocalFileLength=i.Length;
    }
    private static bool NeedsLocalCheck(AppState state,string path)
    {
        if(!File.Exists(path))return false;
        var i=new FileInfo(path);
        return state.LocalSha256 is null || state.LocalFileStamp!=i.LastWriteTimeUtc.Ticks ||
            state.LocalFileLength!=i.Length || state.LastLocalContentCheckUtc is null ||
            DateTimeOffset.UtcNow-state.LastLocalContentCheckUtc>=TimeSpan.FromHours(24);
    }
    private void DetectLocal(AppState state)
    {
        if(!NeedsLocalCheck(state,settings.LocalFile))return;
        KdbxInspector.VerifyPublicHeader(settings.LocalFile);
        var sha=Hashes.Sha256(settings.LocalFile);
        if(sha!=state.LocalSha256)
        {
            var matches=_versions.Values.Where(v=>v.Sha256==sha).ToArray();
            if(state.LocalVersionId is null && matches.Length==1)
                state.LocalVersionId=matches[0].VersionId;
            else
            {
                string[] parents=state.LocalVersionId is null?[]:new[]{state.LocalVersionId};
                state.LocalVersionId=Capture(settings.LocalFile,state,parents,"local").VersionId;
            }
            state.LocalSha256=sha;
        }
        Stamp(state,settings.LocalFile);
        state.LastLocalContentCheckUtc=DateTimeOffset.UtcNow;
    }
    // Downloads small metadata only for previously unseen versions; historical parents
    // are followed on first join, not on unchanged polls.
    private async Task LoadVersionAsync(Replica r,string id,string vault,CancellationToken ct)
    {
        if(_versions.ContainsKey(id))return;
        if(!Guid.TryParseExact(id,"N",out _))throw new SyncSafetyException("Invalid remote version ID");
        var temp=Temp();
        try
        {
            if(!await r.Store.DownloadAsync(VersionMeta(r.Config,id),temp,ct))
                throw new SyncSafetyException("Remote pointer references missing version metadata: "+id);
            var v=JsonSerializer.Deserialize(File.ReadAllText(temp),SyncJson.Default.VersionInfo)
                ??throw new SyncSafetyException("Invalid remote version JSON");
            Validate(v,vault);
            if(v.VersionId!=id)throw new SyncSafetyException("Remote version ID mismatch");
            SyncStorage.SaveJson(Path.Combine(HistoryDir,id+".json"),v,SyncJson.Default.VersionInfo);
            _versions[id]=v;
            r.Cursor.KnownVersions.Add(id);
            foreach(var parent in v.Parents)
            {
                // Pruned ancestry is allowed. Missing parents are never assumed safe
                // for auto-merging or overwriting another independent branch.
                try{await LoadVersionAsync(r,parent,vault,ct);}
                catch(SyncSafetyException ex) when(ex.Message.StartsWith("Remote pointer references missing version metadata",StringComparison.Ordinal)){}
            }
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }

    private async Task ScanAsync(Replica r,AppState state,CancellationToken ct)
    {
        var old=r.Cursor;
        if(old.LastDeviceDiscoveryUtc is null)
        {
            // Reject an existing incompatible remote directory layout.
            var incompatible=Temp();
            try
            {
                if(await r.Store.DownloadAsync(r.Config.RemoteDirectory+"/state/database.json",incompatible,ct))
                    throw new SyncSafetyException("Incompatible remote directory. Select a new empty directory.");
            }
            finally{if(File.Exists(incompatible))File.Delete(incompatible);}
        }
        // One metadata probe for the shared topology per due cloud. Unlike
        // per-device pointers, this detects a newly joined PC immediately.
        var topologyToken=await RevisionAsync(r.Store,r.Config.RemoteDirectory+"/topology.json",ct);
        if(topologyToken!=old.TopologyRevisionToken)
        {
            old.TopologyRevisionToken=topologyToken;
            if(topologyToken is not null)state.TopologyRefreshNeeded=true;
        }
        r.Revision=await RevisionAsync(r.Store,WorkingKey(r.Config),ct);
        r.WorkingExists=r.Revision is not null;
        var discover=old.LastDeviceDiscoveryUtc is null || r.Revision!=old.WorkingRevision ||
            DateTimeOffset.UtcNow-old.LastDeviceDiscoveryUtc>TimeSpan.FromHours(24);
        var deviceIds=new HashSet<string>(old.DeviceRevisions.Keys,StringComparer.Ordinal);
        var announced=new HashSet<string>(StringComparer.Ordinal);
        var directory=r.Config.RemoteDirectory+"/devices";
        IReadOnlyList<RemoteFile>? inventory=null;
        if(discover || r.Store is not IConditionalWriteStore)
        {
            inventory=await r.Store.ListAsync(directory,ct);
            foreach(var f in inventory)
            {
                var name=Path.GetFileName(f.Path);
                if(name.EndsWith(".json",StringComparison.Ordinal) &&
                    Guid.TryParseExact(name[..^5],"N",out _))deviceIds.Add(name[..^5]);
            }
            old.LastDeviceDiscoveryUtc=DateTimeOffset.UtcNow;
        }
        var topology=TopologyCoordinator.Load(settings);
        foreach(var device in deviceIds)
        {
            if(topology?.Computers.FirstOrDefault(x=>x.Id==device) is {Active:false})continue;
            ct.ThrowIfCancellationRequested();
            var key=DeviceKey(r.Config,device);
            string? revision;
            if(inventory is not null && r.Store is not IConditionalWriteStore)
            {
                var item=inventory.FirstOrDefault(f=>f.Path==key);
                revision=item is null?null:item.ModifiedUtc==default
                    ?"unknown-"+Guid.NewGuid().ToString("N")
                    :item.ModifiedUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else revision=await RevisionAsync(r.Store,key,ct);
            if(revision is null){old.DeviceRevisions.Remove(device);continue;}
            if(old.DeviceRevisions.TryGetValue(device,out var known) && known==revision)continue;
            var tmp=Temp();
            try
            {
                if(!await r.Store.DownloadAsync(key,tmp,ct))continue;
                var pointer=JsonSerializer.Deserialize(File.ReadAllText(tmp),SyncJson.Default.V2DevicePointer)
                    ??throw new SyncSafetyException("Empty device pointer");
                if(pointer.SchemaVersion!=2 || pointer.InstallationId!=device ||
                   !Guid.TryParseExact(pointer.VaultId,"N",out _))
                    throw new SyncSafetyException("Malformed v2 device pointer");
                if(state.VaultId is null)state.VaultId=pointer.VaultId;
                if(state.VaultId!=pointer.VaultId)
                    throw new SyncSafetyException("Foreign vault ID in cloud "+r.Config.Name);
                announced.Add(pointer.VersionId);
                if(pointer.TopologyRevision>state.RemoteTopologyRevision)
                {state.RemoteTopologyRevision=pointer.TopologyRevision;state.TopologyRefreshNeeded=true;}
                try {await LoadVersionAsync(r,pointer.VersionId,pointer.VaultId,ct);}
                catch(SyncSafetyException ex) when(ex.Message.StartsWith(
                    "Remote pointer references missing version metadata",StringComparison.Ordinal))
                {
                    // Device was offline beyond retention; an eventual new commit
                    // becomes an unknown-ancestry conflict, never an auto-overwrite.
                    log.Warn("Pruned pointer from device "+device+" in "+r.Config.Name);
                }
                old.DeviceRevisions[device]=revision;
            }
            finally{if(File.Exists(tmp))File.Delete(tmp);}
        }
        // A pointer from our own last scan is only a hint; for mutable working KDBX
        // we independently detect saves made by KeePassium or other clients.
        if(r.Revision!=old.WorkingRevision)
        {
            if(r.Revision is not null)
            {
                var temp=Temp();
                try
                {
                    if(!await r.Store.DownloadAsync(WorkingKey(r.Config),temp,ct))
                        throw new SyncSafetyException("Working KDBX vanished while downloading");
                    KdbxInspector.VerifyPublicHeader(temp);
                    var sha=Hashes.Sha256(temp);
                    var match=_versions.Values.FirstOrDefault(v=>v.Sha256==sha);
                    // A direct revert to older bytes is still a NEW change unless a
                    // registered device announced that exact known version.
                    if(match is null || (old.WorkingRevision is not null &&
                        old.WorkingVersionId!=match.VersionId && !announced.Contains(match.VersionId)))
                    {
                        if(state.VaultId is null)state.VaultId=_fallbackVaultId??Guid.NewGuid().ToString("N");
                        string[] parents=old.WorkingVersionId is null?[]:new[]{old.WorkingVersionId};
                        match=Capture(temp,state,parents,"external:"+r.Config.Name);
                    }
                    r.WorkingVersion=match.VersionId;
                    r.WorkingSha=sha;
                    old.WorkingVersionId=match.VersionId;
                }
                finally{if(File.Exists(temp))File.Delete(temp);}
            }
            else old.WorkingVersionId=null;
            old.WorkingRevision=r.Revision;
        }
        else r.WorkingVersion??=old.WorkingVersionId;
        if(state.VaultId is not null)
            foreach(var v in _versions.Values)Validate(v,state.VaultId);
    }

    private string[] Heads()
    {
        var parents=_versions.Values.SelectMany(v=>v.Parents).ToHashSet(StringComparer.Ordinal);
        return _versions.Keys.Where(k=>!parents.Contains(k)).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
    }
    private async Task<string> ObtainContentAsync(string id,IReadOnlyList<Replica> replicas,CancellationToken ct)
    {
        var v=_versions[id];
        var local=Path.Combine(BackupDir,v.BackupName);
        if(File.Exists(local) && Hashes.Sha256(local)==v.Sha256)return local;
        foreach(var r in replicas)
        {
            var temporary=Temp();
            try
            {
                if(!await r.Store.DownloadAsync(VersionBody(r.Config,id),temporary,ct))continue;
                if(Hashes.Sha256(temporary)!=v.Sha256)throw new SyncSafetyException("Downloaded version damaged: "+id);
                AtomicCopy(temporary,local);
                return local;
            }
            catch(Exception e) when(e is IOException or HttpRequestException){log.Warn("Backup unavailable in "+r.Config.Name);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        throw new SyncSafetyException("No available content for version: "+id);
    }
    private async Task InstallAsync(VersionInfo v,AppState state,IReadOnlyList<Replica> replicas,CancellationToken ct)
    {
        var data=await ObtainContentAsync(v.VersionId,replicas,ct);
        if(File.Exists(settings.LocalFile) && state.LocalSha256 is not null &&
           Hashes.Sha256(settings.LocalFile)!=state.LocalSha256)
            throw new SyncSafetyException("Local KDBX changed while installing remote version");
        var intent=new LocalInstallIntent{SchemaVersion=2,Kind="pull",VaultId=state.VaultId!,
            InstallationId=state.InstallationId,OldVersionId=state.LocalVersionId,
            OldSha256=state.LocalSha256,NewVersion=v};
        SyncStorage.SaveJsonAtomic(PendingFile,intent,SyncJson.Default.LocalInstallIntent);
        AtomicCopy(data,settings.LocalFile);
        state.LocalVersionId=v.VersionId;state.LocalSha256=v.Sha256;
        state.LastChangeUtc=DateTimeOffset.UtcNow;
        Stamp(state,settings.LocalFile);
        state.LastLocalContentCheckUtc=DateTimeOffset.UtcNow;
        SyncStorage.SaveState(settings.StateFile,state);
        File.Delete(PendingFile);
        onActivity?.Invoke("Получена версия из облака");
    }
    private void RecoverInstall(AppState state)
    {
        if(!File.Exists(PendingFile))return;
        var pending=JsonSerializer.Deserialize(File.ReadAllText(PendingFile),SyncJson.Default.LocalInstallIntent)
            ??throw new SyncSafetyException("Broken v2 local install journal");
        if(pending.SchemaVersion!=2 || pending.VaultId!=state.VaultId)
            throw new SyncSafetyException("Foreign v2 local install journal");
        var current=File.Exists(settings.LocalFile)?Hashes.Sha256(settings.LocalFile):null;
        if(current==pending.NewVersion.Sha256)
        {
            state.LocalVersionId=pending.NewVersion.VersionId;
            state.LocalSha256=current;
            if(pending.Kind=="merge") {state.Conflict=false;state.ConflictVersionIds=[];}
            Stamp(state,settings.LocalFile);
            SyncStorage.SaveState(settings.StateFile,state);
            File.Delete(PendingFile);
            return;
        }
        if(current==pending.OldSha256)
        {
            // A failed install before replacement can safely repeat next cycle.
            File.Delete(PendingFile);return;
        }
        throw new SyncSafetyException("Local KDBX changed during interrupted installation; manual recovery required");
    }

    private async Task PublishAsync(Replica r,VersionInfo head,AppState state,
        IReadOnlyList<Replica> replicas,CancellationToken ct)
    {
        var needVersions=_versions.Values.Any(v=>!r.Cursor.KnownVersions.Contains(v.VersionId));
        var needPointer=r.Cursor.AnnouncedVersionId!=head.VersionId ||
            !r.Cursor.DeviceRevisions.ContainsKey(state.InstallationId);
        var needWorking=r.WorkingVersion!=head.VersionId;
        if(!needVersions && !needPointer && !needWorking)return; // Zero writes on the idle path.
        if(needVersions)await r.Store.EnsureDirectoryAsync(r.Config.RemoteDirectory+"/versions",ct);
        if(needPointer)await r.Store.EnsureDirectoryAsync(r.Config.RemoteDirectory+"/devices",ct);
        // Ship only locally available versions missing from this replica, never
        // download every old remote backup on each poll.
        foreach(var v in _versions.Values.OrderBy(x=>x.CreatedUtc))
        {
            if(r.Cursor.KnownVersions.Contains(v.VersionId))continue;
            var body=Path.Combine(BackupDir,v.BackupName);
            if(!File.Exists(body))body=await ObtainContentAsync(v.VersionId,replicas,ct);
            var meta=Path.Combine(HistoryDir,v.VersionId+".json");
            var remoteBody=VersionBody(r.Config,v.VersionId);
            var remoteMeta=VersionMeta(r.Config,v.VersionId);
            // A network timeout after successful creation is resolved by checking
            // existence; never replace an immutable version with different bytes.
            var exists=await RevisionAsync(r.Store,remoteMeta,ct);
            if(exists is null)
            {
                if(await RevisionAsync(r.Store,remoteBody,ct) is null)
                    await r.Store.UploadAsync(body,remoteBody,WriteMode.CreateNew,ct);
                if(PublicationHookForTests is not null)
                    await PublicationHookForTests(r.Config.Name,"body-created",ct);
                await r.Store.UploadAsync(meta,remoteMeta,WriteMode.CreateNew,ct);
            }
            r.Cursor.KnownVersions.Add(v.VersionId);
        }
        var pointerFile=Temp();
        try
        {
            var pointer=new V2DevicePointer{VaultId=state.VaultId!,InstallationId=state.InstallationId,
                VersionId=head.VersionId,UpdatedUtc=DateTimeOffset.UtcNow,
                TopologyRevision=state.RemoteTopologyRevision};
            SyncStorage.SaveJson(pointerFile,pointer,SyncJson.Default.V2DevicePointer);
            var device=DeviceKey(r.Config,state.InstallationId);
            // Never rewrite the per-device pointer on a quiet poll.
            if(needPointer)
            {
                await r.Store.UploadAsync(pointerFile,device,WriteMode.Replace,ct);
                r.Cursor.DeviceRevisions[state.InstallationId]=await RevisionAsync(r.Store,device,ct)??"";
                r.Cursor.AnnouncedVersionId=head.VersionId;
            }
        }
        finally{if(File.Exists(pointerFile))File.Delete(pointerFile);}
        if(r.WorkingVersion!=head.VersionId)
        {
            // Authoritative immutable KDBX exists before any mutable overwrite.
            if(PublicationHookForTests is not null)
                await PublicationHookForTests(r.Config.Name,"before-working-replace",ct);
            var source=await ObtainContentAsync(head.VersionId,replicas,ct);
            if(r.Store is IConditionalWriteStore conditional &&
                (r.Revision is null || r.Revision.StartsWith('"')))
                await conditional.UploadConditionalAsync(source,WorkingKey(r.Config),r.Revision,ct);
            else
            {
                var now=await RevisionAsync(r.Store,WorkingKey(r.Config),ct);
                if(now!=r.Revision)throw new SyncSafetyException("Concurrent remote working edit");
                await r.Store.UploadAsync(source,WorkingKey(r.Config),WriteMode.Replace,ct);
            }
            r.Cursor.WorkingRevision=await RevisionAsync(r.Store,WorkingKey(r.Config),ct);
            r.Cursor.WorkingVersionId=head.VersionId;
            onActivity?.Invoke("Передана KDBX: "+r.Config.Name);
        }
    }

    public Task RunOnceAsync(CancellationToken ct,bool forceAll=false,
        IReadOnlySet<string>? forceCloudIds=null)
        =>RunPreparedAsync(ct,forceAll,forceCloudIds,null);

    // A completed background scan is staged, never committed outside the host Gate.
    // The coordinator still performs a fresh safety sweep of all peers when the
    // staged result discovers a new or divergent KDBX version.
    internal async Task RunPreparedAsync(CancellationToken ct,bool forceAll,
        IReadOnlySet<string>? forceCloudIds,
        IReadOnlyDictionary<string,ScanOutcome>? preparedScans)
    {
        foreach(var dir in new[]{BackupDir,HistoryDir,ConflictDir})Directory.CreateDirectory(dir);
        // Staging files are NOT committed history. Background per-cloud workers
        // may be using these directories while a different cloud is committing.
        // Never delete a currently active stage in another worker's cycle.
        var abandoned=Path.Combine(settings.DataDirectory,"scan-stage");
        if(Directory.Exists(abandoned) &&
           (File.GetAttributes(abandoned)&FileAttributes.ReparsePoint)==0)
            foreach(var path in Directory.EnumerateDirectories(abandoned))
            {
                try
                {
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0 &&
                       Directory.GetLastWriteTimeUtc(path)<DateTime.UtcNow.AddHours(-12))
                        Directory.Delete(path,true);
                }
                catch(IOException e){log.Warn("Scan staging cleanup: "+e.Message);}
                catch(UnauthorizedAccessException e){log.Warn("Scan staging cleanup: "+e.Message);}
            }
        var state=SyncStorage.LoadState(settings.StateFile);
        state.LastAttemptUtc=DateTimeOffset.UtcNow;
        if(state.TopologyDisabled)
        {
            state.Result="DISABLED";
            SyncStorage.SaveState(settings.StateFile,state);
            return;
        }
        ReadLocalVersions(state.VaultId);
        RecoverInstall(state);
        var active=new List<Replica>();
        // Every provider has its own schedule. A changed local KDBX bypasses
        // every remote poll interval: all replicas are checked before publishing.
        bool localDirty=!File.Exists(settings.LocalFile) || NeedsLocalCheck(state,settings.LocalFile) && (
            !File.Exists(settings.LocalFile) || state.LocalSha256 is null ||
            state.LocalFileStamp!=new FileInfo(settings.LocalFile).LastWriteTimeUtc.Ticks ||
            state.LocalFileLength!=new FileInfo(settings.LocalFile).Length);
        bool fullScan=forceAll || localDirty || state.VaultId is null || state.Result is "NeverRun" or "CONFLICT" or "TOPOLOGY_CHANGED";
        var previousVersions=_versions.Count;
        var commonFirstVaultId=Guid.NewGuid().ToString("N");
        var scanned=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool unsafeReplica=false;
        // Per-cloud staging keeps remote metadata, cursors and newly discovered
        // KDBX versions isolated until a single coordinator validates/commits them.
        async Task<ScanOutcome> ScanCloud(CloudOptions cloud,CancellationToken token)
        {
            if(!fullScan && preparedScans is not null &&
               preparedScans.TryGetValue(cloud.CloudId,out var prepared))
            {
                // An intervening full safety cycle may have already checked
                // this replica. Never roll back its cursor with stale results.
                var previous=state.V2Cursors.TryGetValue(cloud.CloudId,out var cursor)
                    ?cursor.LastScanUtc:null;
                if(previous is null || previous<=prepared.StartedUtc)
                    return prepared;
                log.Info("Discarding superseded background scan: "+cloud.Name);
            }
            return await PrepareCloudScanAsync(cloud,state,commonFirstVaultId,token);
        }
        void PublishIntermediateState(AppState current)
        {
            if(onScanCommitted is null)return;
            try
            {
                onScanCommitted(JsonSerializer.Deserialize(
                    JsonSerializer.Serialize(current,SyncJson.Default.AppState),
                    SyncJson.Default.AppState)!);
            }
            catch(Exception e)
            {
                // UI observers must never abort KDBX reconciliation.
                log.Warn("Intermediate UI notification failed: "+e.GetType().Name);
            }
        }
        void Commit(ScanOutcome outcome)
        {
            var cloud=outcome.Cloud;
            scanned.Add(cloud.Name);
            // Keep the latest scan timing on local state even if this scan fails.
            var lastCursor=state.V2Cursors.TryGetValue(cloud.CloudId,out var savedCursor)
                ?savedCursor:new V2CloudCursor();
            lastCursor.LastScanUtc=DateTimeOffset.UtcNow;
            lastCursor.LastScanElapsedMilliseconds=outcome.ElapsedMilliseconds;
            state.V2Cursors[cloud.CloudId]=lastCursor;
            var status=state.Clouds.TryGetValue(cloud.Name,out var previous)
                ?previous:new ReplicaStatus();
            state.Clouds[cloud.Name]=status;
            state.NextCloudPollUtc[cloud.CloudId]=DateTimeOffset.UtcNow.AddSeconds(
                outcome.Error is null?cloud.PollSeconds:settings.RetrySeconds);
            if(outcome.Engine is not null &&
               outcome.Engine.LastMetrics.TryGetValue(cloud.Name,out var measured))
                LastMetrics[cloud.Name]=measured;
            if(outcome.Error is not null)
            {
                // A failed transport can be retried independently. A safety
                // violation discovered inside a scan must stop all publication:
                // the coordinator must not treat a foreign vault as offline.
                if(outcome.Error is SyncSafetyException)unsafeReplica=true;
                status.State=outcome.Error.Message.Contains("401",StringComparison.OrdinalIgnoreCase) ||
                    outcome.Error.Message.Contains("invalid_grant",StringComparison.OrdinalIgnoreCase)
                    ?"AUTH_ERROR":"ERROR";
                status.Error=outcome.Error.GetType().Name+": "+outcome.Error.Message;
                status.LastCheckedUtc=DateTimeOffset.UtcNow;
                log.Warn("V2 cloud "+cloud.Name+": "+outcome.Error.Message);
                return;
            }
            if(outcome.Replica is null || outcome.Snapshot is null || outcome.Engine is null)
            {
                status.State="NEEDS_AUTH";status.Error="Требуется локальная авторизация";
                status.LastCheckedUtc=DateTimeOffset.UtcNow;
                return;
            }
            var shadow=outcome.Snapshot;
            // Never let one cloud silently replace another vault identity.
            if(state.VaultId is not null && shadow.VaultId is not null &&
               state.VaultId!=shadow.VaultId)
                throw new SyncSafetyException("Different vault identities reported by cloud "+cloud.Name);
            var identity=state.VaultId??shadow.VaultId;
            var committedIds=new List<string>();
            var createdFiles=new List<string>();
            try
            {
            foreach(var (id,v) in outcome.Engine._versions)
            {
                if(identity is not null)Validate(v,identity);
                if(_versions.TryGetValue(id,out var previousVersion))
                {
                    if(JsonSerializer.Serialize(v,SyncJson.Default.VersionInfo)!=
                        JsonSerializer.Serialize(previousVersion,SyncJson.Default.VersionInfo))
                        throw new SyncSafetyException("Different metadata for the same version ID: "+id);
                    continue;
                }
                // The same new external KDBX bytes may have appeared in two
                // clouds before either was registered. Avoid manufacturing two
                // unrelated heads solely due to parallel discovery.
                var duplicate=v.Source.StartsWith("external:",StringComparison.Ordinal) &&
                    File.Exists(Path.Combine(outcome.Stage!,"backup",v.BackupName)) ?
                    _versions.Values.FirstOrDefault(old=>
                        old.Sha256==v.Sha256 && old.VaultId==v.VaultId &&
                        old.Parents.SequenceEqual(v.Parents) &&
                        old.Source.StartsWith("external:",StringComparison.Ordinal)) : null;
                if(duplicate is not null)
                {
                    if(outcome.Replica.WorkingVersion==id)
                        outcome.Replica.WorkingVersion=duplicate.VersionId;
                    if(outcome.Replica.Cursor.WorkingVersionId==id)
                        outcome.Replica.Cursor.WorkingVersionId=duplicate.VersionId;
                    continue;
                }
                var metadata=Path.Combine(outcome.Stage!,"history",id+".json");
                var targetMeta=Path.Combine(settings.DataDirectory,"history",id+".json");
                if(!File.Exists(metadata))
                    throw new SyncSafetyException("Parallel scan lost version metadata: "+id);
                var content=Path.Combine(outcome.Stage!,"backup",v.BackupName);
                if(File.Exists(content))
                {
                    if(Hashes.Sha256(content)!=v.Sha256)
                        throw new SyncSafetyException("Parallel scan version body damaged: "+id);
                    var dest=Path.Combine(settings.DataDirectory,"backup",v.BackupName);
                    if(File.Exists(dest) && Hashes.Sha256(dest)!=v.Sha256)
                        throw new SyncSafetyException("Conflicting version body: "+id);
                    if(!File.Exists(dest))
                    {createdFiles.Add(dest);File.Copy(content,dest,false);}
                }
                if(File.Exists(targetMeta))
                {
                    var old=JsonSerializer.Deserialize(File.ReadAllText(targetMeta),SyncJson.Default.VersionInfo);
                    if(old is null || JsonSerializer.Serialize(old,SyncJson.Default.VersionInfo)!=
                        JsonSerializer.Serialize(v,SyncJson.Default.VersionInfo))
                        throw new SyncSafetyException("Existing version metadata mismatch: "+id);
                }
                else {createdFiles.Add(targetMeta);File.Copy(metadata,targetMeta,false);}
                _versions.Add(id,v);
                committedIds.Add(id);
            }
            }
            catch
            {
                foreach(var id in committedIds)_versions.Remove(id);
                // Discard uncommitted local files only. Existing history/backup
                // files from previous successful cycles are NEVER deleted.
                foreach(var file in createdFiles)
                    try {if(File.Exists(file))File.Delete(file);}
                    catch(IOException){}catch(UnauthorizedAccessException){}
                throw;
            }
            state.VaultId=identity;
            state.RemoteTopologyRevision=Math.Max(state.RemoteTopologyRevision,shadow.RemoteTopologyRevision);
            state.TopologyRefreshNeeded|=shadow.TopologyRefreshNeeded;
            if(shadow.LastChangeUtc is { } change &&
               (state.LastChangeUtc is null || change>state.LastChangeUtc.Value))
                state.LastChangeUtc=change;
            outcome.Replica.Cursor.LastScanUtc=lastCursor.LastScanUtc;
            outcome.Replica.Cursor.LastScanElapsedMilliseconds=outcome.ElapsedMilliseconds;
            outcome.Replica.Cursor.LastSuccessfulScanUtc=DateTimeOffset.UtcNow;
            state.V2Cursors[cloud.CloudId]=outcome.Replica.Cursor;
            status.State="BEHIND";status.Error=null;status.LastCheckedUtc=DateTimeOffset.UtcNow;
            active.Add(outcome.Replica);
            outcome.DetachReplica(); // The coordinator now owns the live store.
        }
        async Task RunScans(IReadOnlyList<CloudOptions> batch)
        {
            // Commit each finished scan immediately. A slow provider must not
            // delay other cards' completed status or their local cursor updates.
            await ParallelCloudRunner.ForEachCompletedAsync(batch,ScanCloud,result=>
            {
                try
                {
                    try {Commit(result);}
                    catch(Exception e) when(e is not OperationCanceledException)
                    {
                        var status=state.Clouds.TryGetValue(result.Cloud.Name,out var old)
                            ?old:new ReplicaStatus();
                        state.Clouds[result.Cloud.Name]=status;
                        status.State="ERROR";status.Error=e.GetType().Name+": "+e.Message;
                        if(e is SyncSafetyException)unsafeReplica=true;
                        status.LastCheckedUtc=DateTimeOffset.UtcNow;
                        state.NextCloudPollUtc[result.Cloud.CloudId]=DateTimeOffset.UtcNow.AddSeconds(settings.RetrySeconds);
                        log.Warn("V2 scan commit "+result.Cloud.Name+": "+e.Message);
                    }
                    var ok=state.Clouds.TryGetValue(result.Cloud.Name,out var latest) &&
                        latest.State is not ("ERROR" or "AUTH_ERROR" or "NEEDS_AUTH");
                    LiveActivity?.End(result.Cloud.CloudId,ok);
                    // A completed cloud becomes visible immediately, even while
                    // another transport is still busy. This copy is UI-only;
                    // the coordinator retains exclusive ownership of the DAG.
                    PublishIntermediateState(state);
                }
                finally {result.Dispose();}
            },ct);
        }
        try
        {
            var now=DateTimeOffset.UtcNow;
            var dueClouds=settings.Clouds.Where(cloud=>
                preparedScans is not null && !fullScan
                ? preparedScans.ContainsKey(cloud.CloudId)
                : CloudScanSchedule.IsDue(cloud.CloudId,
                    state.NextCloudPollUtc.TryGetValue(cloud.CloudId,out var due)?due:null,
                    now,fullScan,forceCloudIds)).ToArray();
            await RunScans(dueClouds);
            // Discovery in one replica triggers a parallel scan of all other
            // replicas before ANY local installation or KDBX publication.
            if(!fullScan && (_versions.Count!=previousVersions || active.Any(r=>r.WasChanged)))
                fullScan=true;
            if(fullScan)
                await RunScans(settings.Clouds.Where(c=>!scanned.Contains(c.Name)).ToArray());
            // Identity/metadata divergence is not an ordinary network timeout:
            // never publish to another replica while a verified incompatible
            // vault or immutable metadata collision is under investigation.
            if(unsafeReplica)
            {
                state.Result="ERROR";
                state.Error="An incompatible cloud identity or version metadata was detected; publication stopped";
                return;
            }
            // An observed topology update might revoke this PC. Stop before
            // publication, let the control plane process it first.
            if(state.TopologyRefreshNeeded)
            {
                state.Result="TOPOLOGY_CHANGED";
                return;
            }
            var configuredNames=settings.Clouds.Select(c=>c.Name);
            if(!fullScan && active.Count==0)
            {
                // Reconcile the cached global headline even when this tick had
                // no network request. An older PARTIAL result must not survive
                // after every configured replica has acknowledged the local head.
                if(ConfiguredCloudStatuses.AllConfirmedAtLocalHead(configuredNames,state))
                {
                    state.Result="SYNCED";
                    state.Error=null;
                }
                else
                {
                    var configured=ConfiguredCloudStatuses.Select(configuredNames,state);
                    if(configured.Any(x=>x.Status.State is "ERROR" or "NEEDS_AUTH" or "AUTH_ERROR"))
                        state.Result=configured.Any(x=>x.Status.State=="SYNCED")?"PARTIAL":"ERROR";
                }
                return;
            }
            // An unchanged remote revision is NOT proof that a previous failed
            // PUT has been retried. The remote working version must actually
            // equal the local head before a no-op poll may end as synchronized.
            var scannedAtHead=state.LocalVersionId is {Length:>0} &&
                active.All(r=>string.Equals(r.WorkingVersion,state.LocalVersionId,
                    StringComparison.Ordinal));
            if(!fullScan && _versions.Count==previousVersions &&
                active.All(r=>!r.WasChanged) && scannedAtHead)
            {
                foreach(var r in active)
                {
                    var cloudStatus=state.Clouds[r.Config.Name];
                    cloudStatus.State="SYNCED";
                    cloudStatus.Error=null;
                    cloudStatus.CurrentVersionId=state.LocalVersionId;
                }
                if(ConfiguredCloudStatuses.AllConfirmedAtLocalHead(configuredNames,state))
                {
                    state.Result="SYNCED";
                    state.Error=null;
                    state.LastSuccessUtc=DateTimeOffset.UtcNow;
                }
                else
                {
                    var configured=ConfiguredCloudStatuses.Select(configuredNames,state);
                    state.Result=configured.Any(x=>x.Status.State=="SYNCED")?"PARTIAL":"ERROR";
                }
                return;
            }
            if(state.VaultId is null)
            {
                if(!File.Exists(settings.LocalFile))throw new SyncSafetyException("No local or remote database for first startup");
                if(active.Count==0)throw new SyncSafetyException("First initialization requires an available cloud");
                state.VaultId=Guid.NewGuid().ToString("N");
            }
            foreach(var v in _versions.Values)Validate(v,state.VaultId);
            DetectLocal(state);
            // Durable local version identity before the first remote PUT.
            SyncStorage.SaveState(settings.StateFile,state);
            var heads=Heads();
            if(heads.Length==0)throw new SyncSafetyException("No initial database content");
            if(heads.Length>1)
            {
                foreach(var id in heads)
                {
                    var source=await ObtainContentAsync(id,active,ct);
                    var target=Path.Combine(ConflictDir,id+".kdbx");
                    if(!File.Exists(target))File.Copy(source,target);
                }
                state.Conflict=true;state.ConflictVersionIds=heads;
                state.Result="CONFLICT";state.Error="Independent versions: manual KeePassXC Merge required";
                foreach(var r in active)state.Clouds[r.Config.Name].State="CONFLICT";
                return;
            }
            var head=_versions[heads[0]];
            if(state.LocalVersionId!=head.VersionId || !File.Exists(settings.LocalFile))
            {
                if(state.LocalVersionId is not null &&
                    !Graph.IsAncestor(state.LocalVersionId,head.VersionId,_versions))
                    throw new SyncSafetyException("Uncertain ancestry: refusing automatic overwrite");
                await InstallAsync(head,state,active,ct);
            }
            // Immutable head is selected exactly once; independent replicas can
            // publish concurrently, but no worker changes AppState or the DAG.
            int success=0;
            await ParallelCloudRunner.ForEachCompletedAsync(active,
                async (r,token)=>
                {
                    using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);
                    budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(30,r.Config.TimeoutSeconds)*5));
                    LiveActivity?.Begin(r.Config.CloudId,"Подготовка публикации…");
                    try {await PublishAsync(r,head,state,active,budget.Token);return (Replica:r,Error:(Exception?)null);}
                    catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                    catch(OperationCanceledException) when(budget.IsCancellationRequested)
                    {return (Replica:r,Error:(Exception?)new TimeoutException("Cloud publication timed out"));}
                    catch(Exception e){return (Replica:r,Error:(Exception?)e);}
                },delivery=>
                {
                    var r=delivery.Replica;
                    LiveActivity?.End(r.Config.CloudId,delivery.Error is null);
                    if(delivery.Error is null)
                    {
                        state.Clouds[r.Config.Name].State="SYNCED";
                        state.Clouds[r.Config.Name].Error=null;
                        state.Clouds[r.Config.Name].CurrentVersionId=head.VersionId;
                        success++;
                    }
                    else
                    {
                        state.Clouds[r.Config.Name].State="ERROR";
                        state.Clouds[r.Config.Name].Error=delivery.Error.GetType().Name+": "+delivery.Error.Message;
                        log.Warn("V2 publish "+r.Config.Name+": "+delivery.Error.Message);
                    }
                    // Display the completed global outcome immediately once
                    // every configured cloud confirms THIS version; don't wait
                    // for an unrelated periodic host pass to repair PARTIAL.
                    if(ConfiguredCloudStatuses.AllConfirmedAtLocalHead(
                        configuredNames,state))
                    {
                        state.Result="SYNCED";
                        state.Error=null;
                        state.LastSuccessUtc=DateTimeOffset.UtcNow;
                    }
                    PublishIntermediateState(state);
                },ct);
            state.Conflict=false;state.ConflictVersionIds=[];
            var confirmed=ConfiguredCloudStatuses.AllConfirmedAtLocalHead(configuredNames,state);
            var anyConfirmed=ConfiguredCloudStatuses.Select(configuredNames,state)
                .Any(x=>x.Status.State=="SYNCED" &&
                    string.Equals(x.Status.CurrentVersionId,state.LocalVersionId,
                        StringComparison.Ordinal));
            state.Result=confirmed?"SYNCED":anyConfirmed?"PARTIAL":"ERROR";
            state.Error=confirmed || anyConfirmed?null:"No cloud confirmed the current version";
            if(success>0)state.LastSuccessUtc=DateTimeOffset.UtcNow;
            if(confirmed && active.Count==settings.Clouds.Count)
                await CleanupAsync(active,state,ct);
        }
        catch(Exception e) when(e is not OperationCanceledException)
        {
            state.Result=state.Conflict?"CONFLICT":"ERROR";
            state.Error=e.Message;log.Warn("V2 cycle: "+e.Message);
        }
        finally
        {
            foreach(var (name,metrics) in LastMetrics)
            {
                if(!state.V2Totals.TryGetValue(name,out var total))
                    state.V2Totals[name]=total=new V2OperationTotals();
                total.Add(metrics);
            }
            SyncStorage.SaveState(settings.StateFile,state);
            foreach(var r in active)r.Store.Dispose();
        }
    }

    private async Task CleanupAsync(List<Replica> clouds,AppState state,CancellationToken ct)
    {
        if(clouds.Count!=settings.Clouds.Count || state.Conflict || _versions.Count<=settings.BackupCopies)return;
        if(clouds.Any(c=>c.Cursor.LastCleanupUtc is not null &&
            DateTimeOffset.UtcNow-c.Cursor.LastCleanupUtc<TimeSpan.FromHours(24)))return;
        var keep=_versions.Values.OrderByDescending(v=>v.CreatedUtc).Take(settings.BackupCopies)
            .Select(v=>v.VersionId).ToHashSet(StringComparer.Ordinal);
        foreach(var v in _versions.Values)
            if(v.CreatedUtc>=DateTimeOffset.UtcNow.AddDays(-settings.BackupDays))keep.Add(v.VersionId);
        if(state.LocalVersionId is not null)keep.Add(state.LocalVersionId);
        foreach(var v in _versions.Values.Where(v=>!keep.Contains(v.VersionId)).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            // A partial deletion is retried; do not remove local metadata until all
            // available replicas have been cleaned.
            var removals=await ParallelCloudRunner.RunAsync(clouds,
                async (r,token)=>
                {
                    try
                    {
                        await r.Store.DeleteAsync(VersionBody(r.Config,v.VersionId),token);
                        await r.Store.DeleteAsync(VersionMeta(r.Config,v.VersionId),token);
                        return (Replica:r,Error:(Exception?)null);
                    }
                    catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                    catch(Exception e){return (Replica:r,Error:(Exception?)e);}
                },ct);
            bool done=true;
            foreach(var removed in removals)
            {
                if(removed.Error is null)removed.Replica.Cursor.KnownVersions.Remove(v.VersionId);
                else {done=false;log.Warn("Pruning "+v.VersionId+" on "+removed.Replica.Config.Name+
                    ": "+removed.Error.Message);}
            }
            if(!done)continue;
            File.Delete(Path.Combine(BackupDir,v.BackupName));
            File.Delete(Path.Combine(HistoryDir,v.VersionId+".json"));
            _versions.Remove(v.VersionId);
        }
        foreach(var r in clouds)r.Cursor.LastCleanupUtc=DateTimeOffset.UtcNow;
    }

    public string PrepareMerge()
    {
        var state=SyncStorage.LoadState(settings.StateFile);
        if(!state.Conflict || state.ConflictVersionIds.Length<2)
            throw new SyncSafetyException("No unresolved v2 conflict");
        var active=ActiveConflictFiles.Read(settings.DataDirectory,state);
        if(active.Length<2 || active.Length!=state.ConflictVersionIds.Length || active.Any(v=>!v.Ready))
            throw new SyncSafetyException("Current conflict files are incomplete: "+
                string.Join("; ",active.Where(v=>!v.Ready).Select(v=>v.FileName+": "+v.Error)));
        var prepared=MergeWorkspaceFileNames.Plan(active);

        // Every invocation gets a fresh directory. No stale conflict copy, previous
        // merge attempt or user-edited result can silently enter the new workspace.
        var parent=Path.Combine(settings.DataDirectory,"merge");
        if(Directory.Exists(parent) &&
            (File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)
            throw new SyncSafetyException("Merge workspace parent must not be a link");
        Directory.CreateDirectory(parent);
        var workspace=Path.Combine(parent,"merge-"+DateTimeOffset.UtcNow.ToString(
            "yyyyMMdd-HHmmss",System.Globalization.CultureInfo.InvariantCulture)+"-"+
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            foreach(var assignment in prepared)
            {
                var item=assignment.Version;
                var source=Path.Combine(ConflictDir,item.FileName);
                var target=Path.Combine(workspace,assignment.FileName);
                var expected=Path.Combine(HistoryDir,item.VersionId+".json");
                var metadata=System.Text.Json.JsonSerializer.Deserialize(
                    File.ReadAllText(expected),SyncJson.Default.VersionInfo)
                    ??throw new SyncSafetyException("Missing current version metadata");
                if(Hashes.Sha256(source)!=metadata.Sha256)
                    throw new SyncSafetyException("Conflict copy changed: "+item.FileName);
                File.Copy(source,target,false);
                if(Hashes.Sha256(target)!=metadata.Sha256)
                    throw new SyncSafetyException("Merge working copy verification failed: "+item.FileName);
            }
            var guide=new List<string>
            {
                "KeyPassXC Cloud Sync — ACTIVE CONFLICT",
                "These are independent WORKING COPIES of the current conflicting versions.",
                "The immutable originals in data/conflicts/ must not be modified.",
                "",
                "Each name contains the VERSION timestamp (local time), not the preparation time.",
                "Clock differences may make the chronological order approximate.",
                "Prepared file  <-  original conflict file (version ID) / version time:",
                ""
            };
            guide.AddRange(prepared.Select(x=>x.FileName+"  <-  "+x.Version.FileName+
                "  ("+x.Version.CreatedUtc!.Value.ToLocalTime().ToString(
                    "yyyy-MM-dd HH:mm:ss zzz",System.Globalization.CultureInfo.InvariantCulture)+")"));
            guide.AddRange(new[]{"",
                "1. Open the prepared copies in KeePassXC and merge the changes manually.",
                "2. Save the verified result to a new KDBX, OR overwrite one prepared WORKING COPY in this folder.",
                "   Never overwrite originals under data/conflicts/.",
                "3. Reopen the saved result in KeePassXC and verify that all desired entries are present.",
                "4. In KeyPassXC Cloud Sync choose Confirm Merge and select the saved result.",
                "   If it is byte-identical to an original branch, a separate explicit confirmation is required.",
                "Historical conflicting versions remain preserved for recovery.",
                ""});
            File.WriteAllLines(Path.Combine(workspace,"README_MERGE.txt"),guide,
                System.Text.Encoding.UTF8);
            return workspace;
        }
        catch
        {
            // The directory was uniquely created here. Nothing user-authored may
            // exist in it before PrepareMerge returns successfully.
            try{Directory.Delete(workspace,true);}catch(IOException){}catch(UnauthorizedAccessException){}
            throw;
        }
    }
    // A result may be saved over a prepared copy in data/merge/. Only immutable
    // originals in data/conflicts/ are forbidden as selection targets. This
    // inspection never decrypts KDBX, and cannot prove that entries were merged.
    // A matching version ID means the selected bytes are exactly an original
    // branch, which the UI must distinguish from a combined database.
    public string? InspectMergeCandidate(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var resolved=Path.GetFullPath(file);
        if(MergeCandidatePaths.IsImmutableOriginal(settings.DataDirectory,resolved))
            throw new SyncSafetyException("An immutable file in data/conflicts/ cannot be a merge result");
        if(!File.Exists(resolved))throw new SyncSafetyException("Selected merge result does not exist");
        if((File.GetAttributes(resolved)&FileAttributes.ReparsePoint)!=0)
            throw new SyncSafetyException("Merge result must not be a symbolic link");
        KdbxInspector.VerifyPublicHeader(resolved);
        var state=SyncStorage.LoadState(settings.StateFile);
        if(!state.Conflict || state.ConflictVersionIds.Length<2 || state.VaultId is null)
            throw new SyncSafetyException("No v2 conflict available for Merge");
        ReadLocalVersions(state.VaultId);
        var sha=Hashes.Sha256(resolved);
        string? matched=null;
        foreach(var id in state.ConflictVersionIds)
        {
            if(!_versions.TryGetValue(id,out var version))
                throw new SyncSafetyException("Conflict history changed since preparation");
            var original=Path.Combine(ConflictDir,id+".kdbx");
            if(!File.Exists(original) || (File.GetAttributes(original)&FileAttributes.ReparsePoint)!=0)
                throw new SyncSafetyException("Original conflict copy missing or unsafe: "+id);
            // Verify against the ORIGINAL metadata, not the current selected
            // working file. A changed immutable source must stop acceptance.
            if(new FileInfo(original).Length!=version.Size || Hashes.Sha256(original)!=version.Sha256)
                throw new SyncSafetyException("Immutable conflict copy changed: "+id);
            if(version.Sha256==sha)matched=id;
        }
        return matched;
    }

    public Task AcceptMergeAsync(string file,bool userConfirmed,CancellationToken ct,
        string? acceptedOriginalVersionId=null)
    {
        ct.ThrowIfCancellationRequested();
        if(!userConfirmed)throw new SyncSafetyException("Merge must be explicitly confirmed");
        // Keep one verified snapshot for both history and installation. KeePassXC
        // may save the selected working file again during acceptance; never use
        // its live bytes twice for different copies of one revision.
        Directory.CreateDirectory(settings.DataDirectory);
        var snapshot=Path.Combine(settings.DataDirectory,
            "merge-accept-"+Guid.NewGuid().ToString("N")+".tmp");
        // Reject direct use of immutable conflict files before copying them.
        InspectMergeCandidate(file);
        try
        {
            File.Copy(file,snapshot,false);
            ct.ThrowIfCancellationRequested();
            // Never capture a partial or concurrently modified KeePassXC save.
            if(Hashes.Sha256(snapshot)!=Hashes.Sha256(file))
                throw new SyncSafetyException("Merge result changed while being accepted; save it and try again");
            var matchingSource=InspectMergeCandidate(snapshot);
            if(matchingSource is not null && matchingSource!=acceptedOriginalVersionId)
                throw new SyncSafetyException(
                    "Selected KDBX is unchanged from an original conflict version. "+
                    "To keep this version and discard independent changes, use the explicit choice in Confirm Merge.");
            var state=SyncStorage.LoadState(settings.StateFile);
            var parents=state.ConflictVersionIds.ToArray();
            // Both original parents are retained even when the user explicitly
            // chooses one branch, so the other branch remains recoverable.
            var merged=Capture(snapshot,state,parents,"merge");
            var intent=new LocalInstallIntent {SchemaVersion=2,Kind="merge",
                VaultId=state.VaultId!,InstallationId=state.InstallationId,
                OldVersionId=state.LocalVersionId,OldSha256=state.LocalSha256,NewVersion=merged};
            SyncStorage.SaveJsonAtomic(PendingFile,intent,SyncJson.Default.LocalInstallIntent);
            AtomicCopy(snapshot,settings.LocalFile);
            state.LocalVersionId=merged.VersionId;state.LocalSha256=merged.Sha256;
            state.Conflict=false;state.ConflictVersionIds=[];
            Stamp(state,settings.LocalFile);
            SyncStorage.SaveState(settings.StateFile,state);
            File.Delete(PendingFile);
        }
        finally
        {
            if(File.Exists(snapshot))File.Delete(snapshot);
        }
        return Task.CompletedTask;
    }

}
