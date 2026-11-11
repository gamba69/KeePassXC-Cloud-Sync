using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyPassXCCloudSync;

// Topology never contains credentials. CloudId and InstallationId are permanent
// identities; labels and INI section names are not identities.
public sealed class TopologyCloud
{
    public string Id {get;set;}="";
    public string Name {get;set;}="";
    public string Provider {get;set;}="";
    public string RemoteDirectory {get;set;}="";
    public string BaseUrl {get;set;}="";
    public string RootPath {get;set;}="";
    public string ClientId {get;set;}="";
    public string S3Region {get;set;}="auto";
    public int TimeoutSeconds {get;set;}=120;
    public int PollSeconds {get;set;}=60;
    public int GoogleFolderCacheSeconds {get;set;}=10;
}
// Read-only tombstone: enough to list deleted clouds without re-adding them
// to the active transport configuration or retaining old access details.
public sealed class TopologyRemovedCloud
{
    public string Id {get;set;}="";
    public string Name {get;set;}="";
    public string Provider {get;set;}="";
    public DateTimeOffset RemovedUtc {get;set;}
}
public sealed class TopologyComputer
{
    public string Id {get;set;}="";
    public string Name {get;set;}="";
    public bool Active {get;set;}=true;
    // An inactive revocation marker prevents removed clients from rejoining.
    public bool Purged {get;set;}
    public DateTimeOffset JoinedUtc {get;set;}
}
public sealed class TopologyDocument
{
    public int SchemaVersion {get;set;}=1;
    public string TopologyId {get;set;}="";
    public int Revision {get;set;}
    public string ChangeId {get;set;}="";
    public DateTimeOffset UpdatedUtc {get;set;}
    public string WriterId {get;set;}="";
    // Shared synchronization defaults are published by an explicit settings save.
    // Null means the field was not published, not a request to clear local settings.
    public int? DefaultCloudPollSeconds {get;set;}
    public int? DebounceMs {get;set;}
    public int? RetrySeconds {get;set;}
    public int StatsPublishMinutes {get;set;}=60;
    public int BackupCopies {get;set;}=20;
    public int BackupDays {get;set;}=30;
    public List<TopologyCloud> Clouds {get;set;}=[];
    public List<TopologyRemovedCloud> RemovedClouds {get;set;}=[];
    // Retain only IDs to prevent cached/late peer usage from resurrecting deleted clouds.
    public List<string> PurgedCloudIds {get;set;}=[];
    public List<TopologyComputer> Computers {get;set;}=[];
}
public sealed class TopologyPending
{
    public string BasedOnChangeId {get;set;}="";
    public bool LeaveAfterPublish {get;set;}
    // Local-only UI hint: identify the queued purge without treating it as published.
    public string? PurgeComputerId {get;set;}
    public string? PurgeCloudId {get;set;}
    public TopologyDocument Desired {get;set;}=new();
}
// A queued purge is hidden only in the local archive UI, never from persistent
// statistics until the shared topology has actually been published.
internal sealed record PendingPurgeInfo(string? ComputerId,string? CloudId);

[JsonSerializable(typeof(TopologyDocument))]
[JsonSerializable(typeof(TopologyPending))]
internal partial class TopologyJson:JsonSerializerContext{}

internal static class TopologyCoordinator
{
    internal static string CachePath(AppOptions cfg)=>Path.Combine(cfg.DataDirectory,"topology.json");
    internal static string PendingPath(AppOptions cfg)=>Path.Combine(cfg.DataDirectory,"topology-pending.json");
    internal static TopologyDocument? Load(AppOptions cfg)
    {
        var file=CachePath(cfg);
        return File.Exists(file)?JsonSerializer.Deserialize(File.ReadAllText(file),TopologyJson.Default.TopologyDocument):null;
    }
    // This is only a presentation hint. The durable archive/usage remains intact
    // until RunAsync publishes the desired topology. A stale or unrelated pending
    // edit must never hide an archived card.
    internal static PendingPurgeInfo? ReadPendingPurge(AppOptions cfg)
    {
        try
        {
            var current=Load(cfg);
            var path=PendingPath(cfg);
            if(current is null || !File.Exists(path))return null;
            var pending=JsonSerializer.Deserialize(File.ReadAllText(path),TopologyJson.Default.TopologyPending);
            if(pending is null || pending.Desired.TopologyId!=current.TopologyId ||
               (pending.BasedOnChangeId!=current.ChangeId && pending.Desired.ChangeId!=current.ChangeId))
                return null;
            var computerId=pending.PurgeComputerId;
            var cloudId=pending.PurgeCloudId;
            if(computerId is not null && !pending.Desired.Computers.Any(c=>
                    c.Id==computerId && !c.Active && c.Purged))computerId=null;
            if(cloudId is not null && (!pending.Desired.PurgedCloudIds.Contains(cloudId,StringComparer.OrdinalIgnoreCase) ||
                    current.Clouds.Any(c=>c.Id==cloudId)))cloudId=null;
            return computerId is null && cloudId is null ? null : new PendingPurgeInfo(computerId,cloudId);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A concurrently replaced pending file must not break page rendering.
            return null;
        }
    }
    private static void Save(AppOptions cfg,TopologyDocument value)
        =>SyncStorage.SaveJsonAtomic(CachePath(cfg),value,TopologyJson.Default.TopologyDocument);
    internal static TopologyCloud Public(CloudOptions c)=>new()
    {
        Id=c.CloudId,Name=c.Name,Provider=c.Provider,RemoteDirectory=c.RemoteDirectory,
        BaseUrl=c.BaseUrl,RootPath=c.RootPath,ClientId=c.ClientId,S3Region=c.S3Region,
        TimeoutSeconds=c.TimeoutSeconds,PollSeconds=c.PollSeconds,
        GoogleFolderCacheSeconds=c.GoogleFolderCacheSeconds
    };
    internal static TopologyDocument New(AppOptions cfg,AppState state)=>new()
    {
        TopologyId=Guid.NewGuid().ToString("N"),Revision=1,ChangeId=Guid.NewGuid().ToString("N"),
        WriterId=state.InstallationId,UpdatedUtc=DateTimeOffset.UtcNow,
        DefaultCloudPollSeconds=cfg.PollSeconds,DebounceMs=cfg.DebounceMs,RetrySeconds=cfg.RetrySeconds,
        StatsPublishMinutes=cfg.StatsPublishMinutes,BackupCopies=cfg.BackupCopies,BackupDays=cfg.BackupDays,
        Clouds=cfg.Clouds.Select(Public).ToList(),
        Computers=[new TopologyComputer{Id=state.InstallationId,Name=PlatformServices.Current.Paths.MachineName,JoinedUtc=DateTimeOffset.UtcNow}]
    };
    internal static void Validate(TopologyDocument topology)
    {
        if(topology.SchemaVersion!=1 || topology.Revision<1 ||
           !Guid.TryParseExact(topology.TopologyId,"N",out _) ||
           !Guid.TryParseExact(topology.ChangeId,"N",out _) ||
           topology.DefaultCloudPollSeconds is <10 or >86400 ||
           topology.DebounceMs is <0 or >600000 ||
           topology.RetrySeconds is <1 or >3600 ||
           topology.StatsPublishMinutes is <1 or >10080 ||
           topology.BackupCopies<20 || topology.BackupDays<30 ||
           topology.Clouds.Count==0 || topology.Clouds.Count>64 ||
           topology.RemovedClouds.Count>10000 ||
           topology.PurgedCloudIds.Count>10000 ||
           topology.PurgedCloudIds.Any(id=>!Guid.TryParseExact(id,"N",out _)) ||
           topology.PurgedCloudIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=topology.PurgedCloudIds.Count ||
           topology.RemovedClouds.Any(c=>topology.PurgedCloudIds.Contains(c.Id,StringComparer.OrdinalIgnoreCase)) ||
           topology.Clouds.Any(c=>topology.PurgedCloudIds.Contains(c.Id,StringComparer.OrdinalIgnoreCase)) ||
           topology.RemovedClouds.Select(c=>c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=topology.RemovedClouds.Count ||
           topology.RemovedClouds.Any(c=>string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name)) ||
           topology.RemovedClouds.Any(c=>topology.Clouds.Any(active=>active.Id==c.Id)) ||
           topology.Computers.Count>100 ||
           topology.Clouds.Select(c=>c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=topology.Clouds.Count ||
           topology.Computers.Select(c=>c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=topology.Computers.Count ||
           topology.Computers.Any(c=>c.Purged && (c.Active || c.Name.Length>0 || c.JoinedUtc!=default)))
            throw new SyncSafetyException("Invalid shared topology");
        foreach(var c in topology.Clouds)
            if(string.IsNullOrWhiteSpace(c.Id) || !CloudConnectionName.IsValid(c.Name) ||
               c.PollSeconds is <10 or >86400 || c.Provider is not
               ("GoogleDrive" or "WebDAV" or "S3" or "CloudflareR2" or "ProtonDrive" or "FileSystemTest" or "FileSystemTestNoCas"))
                throw new SyncSafetyException("Invalid shared topology cloud");
        foreach(var d in topology.Computers)
            if(!Guid.TryParseExact(d.Id,"N",out _))throw new SyncSafetyException("Invalid topology computer ID");
    }
    private static bool SameRemoteLocation(TopologyCloud existing,TopologyCloud current)
    {
        if(current.Provider=="ProtonDrive")
            return existing.RemoteDirectory==current.RemoteDirectory;
        return existing.RemoteDirectory==current.RemoteDirectory && existing.RootPath==current.RootPath;
    }
    internal static bool IsAuthorized(CloudOptions c)=>c.Provider switch
    {
        "S3" or "CloudflareR2" or "WebDAV"=>PlatformServices.Current.Secrets.Exists(c.CredentialTarget),
        "GoogleDrive"=>PlatformServices.Current.Secrets.Exists(c.RefreshCredential),
        "ProtonDrive"=>File.Exists(PlatformServices.Current.Paths.ResolveProtonExecutable(c.Executable)),
        "FileSystemTest" or "FileSystemTestNoCas"=>Environment.GetEnvironmentVariable("KPS_ENABLE_TEST_STORE")=="1",
        _=>false
    };
    private static string Key(CloudOptions c)=>c.RemoteDirectory+"/topology.json";
    private static string Pointer(CloudOptions c,string id)=>c.RemoteDirectory+"/devices/"+id+".json";
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"kps-topology-"+Guid.NewGuid().ToString("N")+".tmp");
    private static async Task<TopologyDocument?> Read(IRemoteStore store,CloudOptions cloud,CancellationToken ct)
    {
        var path=Temp();
        try
        {
            if(!await store.DownloadAsync(Key(cloud),path,ct))return null;
            var result=JsonSerializer.Deserialize(File.ReadAllText(path),TopologyJson.Default.TopologyDocument)
                ??throw new SyncSafetyException("Empty shared topology");
            Validate(result);
            return result;
        }
        finally{if(File.Exists(path))File.Delete(path);}
    }
    private static TopologyDocument Choose(IEnumerable<TopologyDocument> snapshots)
    {
        var all=snapshots.ToArray();
        var id=all[0].TopologyId;
        if(all.Any(x=>x.TopologyId!=id))throw new SyncSafetyException("Different topologies in configured clouds");
        var latest=all.Max(x=>x.Revision);
        var heads=all.Where(x=>x.Revision==latest).ToArray();
        if(heads.Select(x=>x.ChangeId).Distinct(StringComparer.Ordinal).Count()>1)
            throw new SyncSafetyException("Concurrent topology edits: manual resolution required");
        return heads[0];
    }
    private static TopologyDocument Next(TopologyDocument baseDoc,string writer)
    {
        var clone=JsonSerializer.Deserialize(JsonSerializer.Serialize(baseDoc,TopologyJson.Default.TopologyDocument),TopologyJson.Default.TopologyDocument)!;
        clone.Revision++;clone.ChangeId=Guid.NewGuid().ToString("N");
        clone.WriterId=writer;clone.UpdatedUtc=DateTimeOffset.UtcNow;
        return clone;
    }
    internal static void QueueFromSettings(AppOptions cfg)
    {
        var old=Load(cfg);
        if(old is null)return; // First setup creates topology during first successful cloud connection.
        var newClouds=cfg.Clouds.Select(Public).ToArray();
        bool difference=old.Clouds.Count!=newClouds.Length || old.StatsPublishMinutes!=cfg.StatsPublishMinutes ||
            old.DefaultCloudPollSeconds!=cfg.PollSeconds || old.DebounceMs!=cfg.DebounceMs ||
            old.RetrySeconds!=cfg.RetrySeconds ||
            old.BackupDays!=cfg.BackupDays || old.BackupCopies!=cfg.BackupCopies ||
            newClouds.Any(c=>!old.Clouds.Any(x=>x.Id==c.Id && x.Name==c.Name &&
                x.Provider==c.Provider && SameRemoteLocation(x,c) &&
                x.BaseUrl==c.BaseUrl && x.PollSeconds==c.PollSeconds &&
                x.S3Region==c.S3Region && x.ClientId==c.ClientId &&
                x.TimeoutSeconds==c.TimeoutSeconds && x.GoogleFolderCacheSeconds==c.GoogleFolderCacheSeconds));
        if(!difference)return;
        var request=new TopologyPending{BasedOnChangeId=old.ChangeId,Desired=Next(old,old.WriterId)};
        request.Desired.Clouds=newClouds.ToList();
        var activeIds=newClouds.Select(c=>c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var former in old.Clouds.Where(c=>!activeIds.Contains(c.Id)))
        {
            if(!request.Desired.RemovedClouds.Any(c=>c.Id==former.Id))
                request.Desired.RemovedClouds.Add(new TopologyRemovedCloud
                {Id=former.Id,Name=former.Name,Provider=former.Provider,RemovedUtc=DateTimeOffset.UtcNow});
        }
        request.Desired.StatsPublishMinutes=cfg.StatsPublishMinutes;
        request.Desired.DefaultCloudPollSeconds=cfg.PollSeconds;
        request.Desired.DebounceMs=cfg.DebounceMs;
        request.Desired.RetrySeconds=cfg.RetrySeconds;
        request.Desired.BackupDays=cfg.BackupDays;
        request.Desired.BackupCopies=cfg.BackupCopies;
        SyncStorage.SaveJsonAtomic(PendingPath(cfg),request,TopologyJson.Default.TopologyPending);
    }
    internal static void QueueRemoveComputer(AppOptions cfg,string id,bool leave)
    {
        var current=Load(cfg)??throw new SyncSafetyException("Topology not initialized yet");
        if(!current.Computers.Any(d=>d.Id==id && d.Active))throw new SyncSafetyException("Computer not active");
        if(File.Exists(PendingPath(cfg)))
            throw new SyncSafetyException("Есть незавершённое изменение топологии. Дождитесь публикации.");
        var state=SyncStorage.LoadState(cfg.StateFile);
        var next=Next(current,state.InstallationId);
        next.Computers.Single(d=>d.Id==id).Active=false;
        var pending=new TopologyPending{BasedOnChangeId=current.ChangeId,Desired=next,
            LeaveAfterPublish=leave};
        SyncStorage.SaveJsonAtomic(PendingPath(cfg),pending,TopologyJson.Default.TopologyPending);
    }
    // Permanent archive purge: only inactive computers / already removed clouds.
    // This updates the shared topology; no remote deletion is attempted here.
    internal static void QueuePurgeComputer(AppOptions cfg,string id)
    {
        var current=Load(cfg)??throw new SyncSafetyException("Topology not initialized yet");
        if(File.Exists(PendingPath(cfg)))
            throw new SyncSafetyException("Есть незавершённое изменение топологии. Дождитесь публикации.");
        var old=current.Computers.SingleOrDefault(c=>c.Id==id);
        if(old is null || old.Active || old.Purged)
            throw new SyncSafetyException("Можно окончательно удалить только ранее удалённый компьютер.");
        var state=SyncStorage.LoadState(cfg.StateFile);
        if(state.InstallationId==id)
            throw new SyncSafetyException("Нельзя очистить запись текущего компьютера.");
        var next=Next(current,state.InstallationId);
        var tombstone=next.Computers.Single(c=>c.Id==id);
        tombstone.Purged=true;tombstone.Name="";tombstone.JoinedUtc=default;
        Validate(next);
        SyncStorage.SaveJsonAtomic(PendingPath(cfg),
            new TopologyPending{BasedOnChangeId=current.ChangeId,Desired=next,PurgeComputerId=id},
            TopologyJson.Default.TopologyPending);
    }
    internal static void QueuePurgeCloud(AppOptions cfg,string id)
    {
        var current=Load(cfg)??throw new SyncSafetyException("Topology not initialized yet");
        if(File.Exists(PendingPath(cfg)))
            throw new SyncSafetyException("Есть незавершённое изменение топологии. Дождитесь публикации.");
        // Older profiles may have a usage-only archive without a RemovedClouds
        // record. Such a record is also safe to purge if it is not active.
        var archived=current.RemovedClouds.Any(c=>c.Id==id);
        var usage=UsageStatistics.Load(cfg);
        var orphaned=UsageStatistics.All(usage).Any(snapshot=>snapshot.Clouds.ContainsKey(id));
        if(current.Clouds.Any(c=>c.Id==id) || (!archived && !orphaned) ||
           current.PurgedCloudIds.Contains(id,StringComparer.OrdinalIgnoreCase) ||
           !Guid.TryParseExact(id,"N",out _))
            throw new SyncSafetyException("Можно окончательно удалить только ранее удалённое облако с корректным CloudId.");
        var state=SyncStorage.LoadState(cfg.StateFile);
        var next=Next(current,state.InstallationId);
        next.RemovedClouds.RemoveAll(c=>c.Id==id);
        next.PurgedCloudIds.Add(id);
        Validate(next);
        SyncStorage.SaveJsonAtomic(PendingPath(cfg),
            new TopologyPending{BasedOnChangeId=current.ChangeId,Desired=next,PurgeCloudId=id},
            TopologyJson.Default.TopologyPending);
    }
    internal static void QueueRenameComputer(AppOptions cfg,string id,string name)
    {
        if(string.IsNullOrWhiteSpace(name) || name.Length>80 || name.Any(char.IsControl))
            throw new ArgumentException("Invalid computer name",nameof(name));
        var current=Load(cfg)??throw new SyncSafetyException("Topology not initialized yet");
        if(File.Exists(PendingPath(cfg)))
            throw new SyncSafetyException("Есть незавершённое изменение топологии. Дождитесь публикации и повторите переименование.");
        if(!current.Computers.Any(d=>d.Id==id && d.Active))
            throw new SyncSafetyException("Computer not active");
        var state=SyncStorage.LoadState(cfg.StateFile);
        var next=Next(current,state.InstallationId);
        next.Computers.Single(d=>d.Id==id).Name=name;
        var pending=new TopologyPending{BasedOnChangeId=current.ChangeId,Desired=next};
        SyncStorage.SaveJsonAtomic(PendingPath(cfg),pending,TopologyJson.Default.TopologyPending);
    }
    private static CloudDraft FromRemote(TopologyCloud c,CloudDraft? existing)
    {
        var result=existing?.Clone()??new CloudDraft{
            CredentialTarget="KeePassSync/"+c.Provider+"/"+Guid.NewGuid().ToString("N"),
            RefreshTarget="KeePassSync/Google/"+Guid.NewGuid().ToString("N")+"/refresh-token"};
        result.CloudId=c.Id;result.Name=c.Name;result.Provider=c.Provider;
        result.Remote=c.RemoteDirectory;
        result.BaseUrl=c.BaseUrl;result.Root=c.Provider=="ProtonDrive"?ProtonConfiguration.Root:c.RootPath;result.ClientId=c.ClientId;
        result.S3Region=c.S3Region;result.Poll=c.PollSeconds;result.Timeout=c.TimeoutSeconds;
        result.GoogleFolderCacheSeconds=c.GoogleFolderCacheSeconds;
        return result;
    }
    private static bool Apply(AppOptions cfg,TopologyDocument remote,bool joining)
    {
        var draft=SettingsDraft.Load(cfg.ConfigPath);
        var existing=draft.Clouds.ToArray();
        draft.Clouds=remote.Clouds.Select(c=>FromRemote(c,existing.FirstOrDefault(x=>x.CloudId==c.Id)
            ??(joining?existing.FirstOrDefault(x=>x.Provider==c.Provider &&
                (c.Provider=="ProtonDrive"
                    ?x.Remote==c.RemoteDirectory
                    :x.Remote==c.RemoteDirectory && x.Root==c.RootPath) &&
                x.BaseUrl==c.BaseUrl):null))).ToList();
        // Optional shared fields do not override local configuration when absent.
        if(remote.DefaultCloudPollSeconds is int defaultPoll)draft.Poll=defaultPoll;
        if(remote.DebounceMs is int debounce)draft.Debounce=debounce;
        if(remote.RetrySeconds is int retry)draft.Retry=retry;
        draft.StatsPublish=remote.StatsPublishMinutes;
        draft.Copies=remote.BackupCopies;draft.Days=remote.BackupDays;
        var before=File.ReadAllText(cfg.ConfigPath);
        var after=SettingsStore.Ini(draft);
        if(before==after)return false;
        // Keep local paths and credentials; shared topology never contains secrets.
        // Received, validated topology is authoritative. Saving its shared
        // settings must not enqueue a second (possibly conflicting) local edit.
        SettingsStore.Save(draft,cfg.ConfigPath,cfg.DataDirectory,
            queueSharedTopologyUpdate:false);
        return true;
    }
    private static async Task Publish(IRemoteStore store,CloudOptions c,TopologyDocument doc,
        TopologyDocument? remote,string? token,bool casReady,AppState state,CancellationToken ct,
        bool announceKdbxPointer)
    {
        if(remote is not null && remote.ChangeId==doc.ChangeId)return;
        if(remote is not null && remote.Revision>=doc.Revision)
            throw new SyncSafetyException("Topology changed while saving; review configuration");
        var tmp=Temp();
        try
        {
            SyncStorage.SaveJson(tmp,doc,TopologyJson.Default.TopologyDocument);
            if(casReady && store is IConditionalWriteStore cas)
                await cas.UploadConditionalAsync(tmp,Key(c),token,ct);
            else await store.UploadAsync(tmp,Key(c),remote is null?WriteMode.CreateNew:WriteMode.Replace,ct);
            // Piggyback topology revision onto the existing per-device pointer.
            // Idle polls do not request topology.json separately.
            if(announceKdbxPointer && state.LocalVersionId is {Length:>0} ver)
            {
                var announce=new V2DevicePointer{VaultId=state.VaultId??"",InstallationId=state.InstallationId,
                    VersionId=ver,UpdatedUtc=DateTimeOffset.UtcNow,TopologyRevision=doc.Revision};
                SyncStorage.SaveJsonAtomic(tmp,announce,SyncJson.Default.V2DevicePointer);
                await store.UploadAsync(tmp,Pointer(c,state.InstallationId),WriteMode.Replace,ct);
            }
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    // After successfully applying a topology, reconcile each cloud's observed
    // revision token with the installed document. Without this step a joining
    // computer sees its own registration as a *new* remote topology change on
    // the very next sync cycle and postpones the initial KDBX download.
    //
    // The two token reads bracket the document read. Do not acknowledge a
    // token if a concurrent writer changed the object or the document is not
    // the one we installed. This runs only for topology changes, never idle polls.
    private static async Task RememberAppliedTokens(AppOptions cfg,SyncLog log,
        AppState state,TopologyDocument installed,
        IEnumerable<CloudOptions> clouds,CancellationToken ct)
    {
        var results=await ParallelCloudRunner.RunAsync(clouds.ToArray(),async (cloud,token)=>
        {
            using var budget=CloudDeadline.Start(cloud,token);
            try
            {
                using var store=new V2Engine(cfg,log).CreateStore(cloud);
                var key=Key(cloud);
                var before=await V2Engine.RevisionAsync(store,key,budget.Token);
                if(before is null)return (Cloud:cloud,Token:(string?)null,Error:(Exception?)null);
                var document=await Read(store,cloud,budget.Token);
                var after=await V2Engine.RevisionAsync(store,key,budget.Token);
                return (Cloud:cloud,Token:before==after && document?.TopologyId==installed.TopologyId &&
                    document.ChangeId==installed.ChangeId?after:null,Error:(Exception?)null);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(OperationCanceledException) when(budget.IsCancellationRequested)
            {return (Cloud:cloud,Token:(string?)null,Error:(Exception?)new TimeoutException("Topology token reconciliation timed out"));}
            catch(Exception e){return (Cloud:cloud,Token:(string?)null,Error:(Exception?)e);}
        },ct);
        foreach(var result in results)
        {
            if(result.Error is not null)
                log.Warn("Topology token reconciliation "+result.Cloud.Name+": "+result.Error.Message);
            if(result.Token is null)continue;
            if(!state.V2Cursors.TryGetValue(result.Cloud.CloudId,out var cursor))
                state.V2Cursors[result.Cloud.CloudId]=cursor=new V2CloudCursor();
            cursor.TopologyRevisionToken=result.Token;
        }
    }
    // Runs after an ordinary sync cycle. Only startup, topology edits and an
    // observed higher revision require reading topology bodies from the cloud.
    internal static async Task<(bool Reload,bool Left)> RunAsync(AppOptions cfg,SyncLog log,CancellationToken ct,
        bool force=false,bool announceKdbxPointer=true)
    {
        var state=SyncStorage.LoadState(cfg.StateFile);
        if(state.TopologyDisabled)return (false,false);
        var cache=Load(cfg);
        var joining=cache is null;
        var pendingFile=PendingPath(cfg);
        var pending=File.Exists(pendingFile)
            ?JsonSerializer.Deserialize(File.ReadAllText(pendingFile),TopologyJson.Default.TopologyPending):null;
        bool need=force || state.TopologyRefreshNeeded || cache is null || pending is not null ||
            state.RemoteTopologyRevision>(cache?.Revision??0);
        if(!need)return(false,false);
        var seen=new List<(CloudOptions Cloud,TopologyDocument? Document,string? Token,bool CasReady)>();
        var discovered=await ParallelCloudRunner.RunAsync(cfg.Clouds,async (cloud,token)=>
        {
            if(!IsAuthorized(cloud))return (Cloud:cloud,Document:(TopologyDocument?)null,
                Token:(string?)null,CasReady:false,Available:false,Error:(Exception?)null);
            using var budget=CloudDeadline.Start(cloud,token);
            try
            {
                using var store=new V2Engine(cfg,log).CreateStore(cloud);
                string? revision=null;bool canCas=false;
                if(store is IConditionalWriteStore cas)
                {
                    try{revision=await cas.ReadTokenAsync(Key(cloud),budget.Token);canCas=true;}
                    catch(SyncSafetyException e) when(e.Message.StartsWith(
                        "WebDAV did not provide a strong ETag",StringComparison.Ordinal)){}
                }
                return (Cloud:cloud,Document:await Read(store,cloud,budget.Token),Token:revision,
                    CasReady:canCas,Available:true,Error:(Exception?)null);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(OperationCanceledException) when(budget.IsCancellationRequested)
            {return (Cloud:cloud,Document:(TopologyDocument?)null,Token:(string?)null,
                CasReady:false,Available:false,Error:(Exception?)new TimeoutException("Topology read timed out"));}
            catch(Exception e){return (Cloud:cloud,Document:(TopologyDocument?)null,
                Token:(string?)null,CasReady:false,Available:false,Error:(Exception?)e);}
        },ct);
        foreach(var item in discovered)
        {
            if(item.Error is not null)log.Warn("Topology read "+item.Cloud.Name+": "+item.Error.Message);
            if(item.Available)seen.Add((item.Cloud,item.Document,item.Token,item.CasReady));
        }
        if(seen.Count==0)return(false,false);
        var existing=seen.Where(x=>x.Document is not null).Select(x=>x.Document!).ToArray();
        var remote=existing.Length>0?Choose(existing):null;
        if(cache is not null && remote is not null && cache.TopologyId!=remote.TopologyId)
            throw new SyncSafetyException("Remote topology identity mismatch");
        var candidate=remote??cache??New(cfg,state);
        if(cache is not null && remote is null)
            throw new SyncSafetyException("Remote topology missing: refusing to publish possibly stale cache");
        if(pending is not null)
        {
            if(remote is not null && pending.BasedOnChangeId!=remote.ChangeId &&
                pending.Desired.ChangeId!=remote.ChangeId)
                throw new SyncSafetyException("Topology edited elsewhere; pending changes were NOT overwritten");
            candidate=pending.Desired;
            candidate.WriterId=state.InstallationId;
            Validate(candidate);
        }
        else if(remote is not null && cache is null)
        {
            // New participant discovers an existing topology. Previously removed
            // installations are never allowed to auto-join under the same ID.
            var match=remote.Computers.FirstOrDefault(d=>d.Id==state.InstallationId);
            if(match is not null && !match.Active){DisableLocal(cfg,state);return(false,true);}
            if(match is null)
            {
                candidate=Next(remote,state.InstallationId);
                candidate.Computers.Add(new TopologyComputer{Id=state.InstallationId,
                    Name=PlatformServices.Current.Paths.MachineName,JoinedUtc=DateTimeOffset.UtcNow});
            }
        }
        else if(remote is not null && cache is not null)
        {
            if(remote.Computers.FirstOrDefault(x=>x.Id==state.InstallationId) is {Active:false})
            {DisableLocal(cfg,state);return(false,true);}
        }
        Validate(candidate);
        bool changedRemote=remote is null || candidate.ChangeId!=remote.ChangeId;
        int success=0;
        if(changedRemote || seen.Any(x=>x.Document is null || x.Document.Revision<candidate.Revision))
        {
            var published=await ParallelCloudRunner.RunAsync(seen,async (item,token)=>
            {
                using var budget=CloudDeadline.Start(item.Cloud,token,5);
                try
                {
                    using var store=new V2Engine(cfg,log).CreateStore(item.Cloud);
                    await Publish(store,item.Cloud,candidate,item.Document,item.Token,
                        item.CasReady,state,budget.Token,announceKdbxPointer);
                    return (Cloud:item.Cloud,Error:(Exception?)null);
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                catch(OperationCanceledException) when(budget.IsCancellationRequested)
                {return (Cloud:item.Cloud,Error:(Exception?)new TimeoutException("Topology publication timed out"));}
                catch(Exception e){return (Cloud:item.Cloud,Error:(Exception?)e);}
            },ct);
            foreach(var result in published)
                if(result.Error is null)success++;
                else log.Warn("Topology publish "+result.Cloud.Name+": "+result.Error.Message);
        }
        if(changedRemote && success==0)return(false,false);
        Save(cfg,candidate);
        // Once the topology update reached a cloud, remove purged usage locally.
        // No retained statistical snapshot can make a purged object visible again.
        UsageStatistics.PruneSaved(cfg,candidate);
        state.RemoteTopologyRevision=candidate.Revision;
        state.TopologyRefreshNeeded=false;
        // The topology writer (including a newly joined PC) must not mistake
        // its own successful update for a new concurrent edit on the next poll.
        await RememberAppliedTokens(cfg,log,state,candidate,seen.Select(x=>x.Cloud),ct);
        SyncStorage.SaveState(cfg.StateFile,state);
        // An interrupted previous run may already have published the exact
        // pending revision to every reachable cloud. In that case Publish()
        // correctly performs no writes, but the queued edit must still be
        // acknowledged or every future user edit remains blocked indefinitely.
        // Never clear a pending edit merely because a remote revision is newer:
        // all reachable cloud documents must match its *exact* revision/ID.
        bool pendingCompleted=success==seen.Count || seen.All(x=>
            x.Document is not null && x.Document.TopologyId==candidate.TopologyId &&
            x.Document.Revision==candidate.Revision && x.Document.ChangeId==candidate.ChangeId);
        if(pending is not null && pendingCompleted)File.Delete(pendingFile);
        if(pending?.LeaveAfterPublish==true && pendingCompleted)
        {DisableLocal(cfg,state);return(false,true);}
        state.TopologyError=null;SyncStorage.SaveState(cfg.StateFile,state);
        return(Apply(cfg,candidate,joining),false);
    }
    // Idle mode is CONTROL PLANE ONLY. A paused client still joins topology,
    // sees removals/renames and exchanges stats, but it never reads/writes
    // database.kdbx, versions/, devices/ or the local vault contents.
    internal static async Task<(bool Reload,bool Left,int WaitSeconds)> PollPausedAsync(
        AppOptions cfg,SyncLog log,CancellationToken ct,bool force=false)
    {
        var state=SyncStorage.LoadState(cfg.StateFile);
        if(state.TopologyDisabled)return(false,false,86400);
        var now=DateTimeOffset.UtcNow;
        bool changed=force;
        var due=cfg.Clouds.Where(cloud=>force ||
            !state.NextCloudPollUtc.TryGetValue(cloud.CloudId,out var deadline) || deadline<=now).ToArray();
        var probes=await ParallelCloudRunner.RunAsync(due,async (cloud,token)=>
        {
            if(!IsAuthorized(cloud))
                return (Cloud:cloud,Revision:(string?)null,Unauthorized:true,
                    Totals:(V2OperationTotals?)null,Error:(Exception?)null);
            V2MeteredStore? metered=null;
            using var budget=CloudDeadline.Start(cloud,token);
            try
            {
                var transport=new V2Engine(cfg,log).CreateStore(cloud);
                metered=transport is IConditionalWriteStore
                    ?new V2MeteredCasStore(transport):new V2MeteredStore(transport);
                var revision=await V2Engine.RevisionAsync(metered,Key(cloud),budget.Token);
                return (Cloud:cloud,Revision:revision,Unauthorized:false,
                    Totals:(V2OperationTotals?)metered.Stats,Error:(Exception?)null);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(OperationCanceledException) when(budget.IsCancellationRequested)
            {return (Cloud:cloud,Revision:(string?)null,Unauthorized:false,
                Totals:(V2OperationTotals?)metered?.Stats,
                Error:(Exception?)new TimeoutException("Paused topology probe timed out"));}
            catch(Exception e){return (Cloud:cloud,Revision:(string?)null,Unauthorized:false,
                Totals:(V2OperationTotals?)metered?.Stats,Error:(Exception?)e);}
            finally{metered?.Dispose();}
        },ct);
        foreach(var probe in probes)
        {
            var cloud=probe.Cloud;
            state.NextCloudPollUtc[cloud.CloudId]=now.AddSeconds(Math.Max(10,cloud.PollSeconds));
            var status=state.Clouds.TryGetValue(cloud.Name,out var previous)?previous:new ReplicaStatus();
            state.Clouds[cloud.Name]=status;
            status.LastCheckedUtc=DateTimeOffset.UtcNow;
            if(probe.Totals is not null)
            {
                if(!state.V2Totals.TryGetValue(cloud.Name,out var sum))
                    state.V2Totals[cloud.Name]=sum=new V2OperationTotals();
                sum.Add(probe.Totals);
            }
            if(probe.Unauthorized)
            {
                status.State="NEEDS_AUTH";status.Error="Облако не авторизовано";
            }
            else if(probe.Error is not null)
            {
                status.State="OFFLINE";status.Error="Проверка топологии: "+probe.Error.Message;
                log.Warn("Paused topology poll "+cloud.Name+": "+probe.Error.Message);
            }
            else
            {
                state.V2Cursors.TryGetValue(cloud.CloudId,out var cursor);
                if(probe.Revision?.StartsWith("unknown-",StringComparison.Ordinal)==true ||
                   probe.Revision!=cursor?.TopologyRevisionToken)changed=true;
                status.State="TOPOLOGY_OK";status.Error=null;
            }
        }
        if(changed)state.TopologyRefreshNeeded=true;
        SyncStorage.SaveState(cfg.StateFile,state);
        // RunAsync reads/writes ONLY topology.json in idle mode, never a device
        // version pointer. It also honors pending edits and remote removals.
        var result=await RunAsync(cfg,log,ct,force:changed,announceKdbxPointer:false);
        state=SyncStorage.LoadState(cfg.StateFile);
        var earliest=cfg.Clouds.Select(c=>state.NextCloudPollUtc.TryGetValue(c.CloudId,out var d)
            ?Math.Max(1,(int)Math.Ceiling((d-DateTimeOffset.UtcNow).TotalSeconds)):1)
            .DefaultIfEmpty(60).Min();
        return(result.Reload,result.Left,earliest);
    }

    private static void DisableLocal(AppOptions cfg,AppState state)
    {
        state.TopologyDisabled=true;
        SyncStorage.SaveState(cfg.StateFile,state);
        if(cfg.Clouds.All(c=>c.Provider is not ("FileSystemTest" or "FileSystemTestNoCas")))
        {
            SettingsStore.SetAutostart(false,cfg.ConfigPath);
            PlatformServices.Current.Secrets.RemoveUnused(Array.Empty<string>(),fullLeave:true);
        }
        // Preserve KDBX, conflicts and historical backups; remove only
        // KeePassSync-owned authorization and control-plane caches.
    }
}
