using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyPassXCCloudSync;

// Stats are informational and never affect version selection or cloud safety.
public sealed class UsageCloud
{
    public string CloudId {get;set;}="";
    public string Name {get;set;}="";
    public V2OperationTotals Total {get;set;}=new();
    public Dictionary<string,V2OperationTotals> Days {get;set;}=new();
}
public sealed class UsageSnapshot
{
    public int SchemaVersion {get;set;}=2;
    public string InstallationId {get;set;}="";
    public string ComputerName {get;set;}="";
    public DateTimeOffset UpdatedUtc {get;set;}
    public Dictionary<string,UsageCloud> Clouds {get;set;}=new(StringComparer.Ordinal);
}
public sealed class UsageLocal
{
    public UsageSnapshot Self {get;set;}=new();
    public Dictionary<string,UsageSnapshot> Peers {get;set;}=new(StringComparer.Ordinal);
    public Dictionary<string,V2OperationTotals> LastEngineTotals {get;set;}=new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastPublishedUtc {get;set;}
}
[JsonSerializable(typeof(UsageSnapshot))]
[JsonSerializable(typeof(UsageLocal))]
internal partial class UsageJson:JsonSerializerContext{}

internal static class UsageStatistics
{
    internal static string PathFor(AppOptions settings)=>Path.Combine(settings.DataDirectory,"usage.json");
    private const int CurrentStatisticsSchema=2;
    // Old snapshots contain only coarse latency buckets. Reset just their
    // duration distributions; do not touch protocol state, backups, traffic,
    // operation counters or per-cloud access settings.
    internal static bool EnsureExactLatency(UsageLocal usage,AppState? baseline=null)
    {
        var changed=false;
        if(usage.Self.SchemaVersion!=CurrentStatisticsSchema)
        {
            foreach(var cloud in usage.Self.Clouds.Values)
            {
                cloud.Total.ResetLatency();
                foreach(var daily in cloud.Days.Values)daily.ResetLatency();
            }
            // The engine stores cumulative telemetry independently. Anchor the
            // existing cursor at the switchover so pre-upgrade measurements
            // cannot leak back into newly reset latency distributions.
            if(baseline is not null)
                foreach(var (name,absolute) in baseline.V2Totals)
                {
                    if(!usage.LastEngineTotals.TryGetValue(name,out var last))
                        usage.LastEngineTotals[name]=last=absolute.Copy();
                    last.LatencyMilliseconds=new Dictionary<long,long>(
                        absolute.LatencyMilliseconds??new());
                    last.SuccessfulMilliseconds=absolute.SuccessfulMilliseconds;
                }
            usage.Self.SchemaVersion=CurrentStatisticsSchema;
            usage.LastPublishedUtc=null;
            changed=true;
        }
        // Preserve historical peer traffic and operation counters while
        // discarding their incompatible coarse latency distributions. Old
        // remote snapshots cannot overwrite these with stale measurements:
        // exchange accepts only schema-2 publications from upgraded peers.
        foreach(var peer in usage.Peers.Values)
            if(peer.SchemaVersion!=CurrentStatisticsSchema)
            {
                foreach(var cloud in peer.Clouds.Values)
                {
                    cloud.Total.ResetLatency();
                    foreach(var daily in cloud.Days.Values)daily.ResetLatency();
                }
                changed=true;
            }
        return changed;
    }
    private static AppState? ExistingState(AppOptions settings)
        =>File.Exists(settings.StateFile)?SyncStorage.LoadState(settings.StateFile):null;
    internal static UsageLocal Load(AppOptions settings)
    {
        var path=PathFor(settings);
        if(!File.Exists(path))return new UsageLocal();
        var usage=JsonSerializer.Deserialize(File.ReadAllText(path),UsageJson.Default.UsageLocal)??new UsageLocal();
        EnsureExactLatency(usage,usage.Self.SchemaVersion!=CurrentStatisticsSchema
            ?ExistingState(settings):null);
        var topology=TopologyCoordinator.Load(settings);
        if(topology is not null)Prune(usage,topology);
        return usage;
    }
    internal static void Save(AppOptions settings,UsageLocal usage)
    {
        EnsureExactLatency(usage,usage.Self.SchemaVersion!=CurrentStatisticsSchema
            ?ExistingState(settings):null);
        var topology=TopologyCoordinator.Load(settings);
        if(topology is not null)Prune(usage,topology);
        SyncStorage.SaveJsonAtomic(PathFor(settings),usage,UsageJson.Default.UsageLocal);
    }
    // Never resurrect a purged record through cached stats or stale remote snapshots.
    internal static bool Prune(UsageLocal usage,TopologyDocument topology)
    {
        var changed=false;
        var purgedClouds=topology.PurgedCloudIds.ToHashSet(StringComparer.Ordinal);
        var purgedComputers=topology.Computers.Where(c=>c.Purged)
            .Select(c=>c.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var id in purgedComputers)
            changed|=usage.Peers.Remove(id);
        foreach(var snapshot in new[]{usage.Self}.Concat(usage.Peers.Values))
            foreach(var id in purgedClouds)
                changed|=snapshot.Clouds.Remove(id);
        return changed;
    }
    internal static void PruneSaved(AppOptions settings,TopologyDocument topology)
    {
        var path=PathFor(settings);
        if(!File.Exists(path))return;
        var usage=JsonSerializer.Deserialize(File.ReadAllText(path),UsageJson.Default.UsageLocal)??new UsageLocal();
        var transitioned=EnsureExactLatency(usage,usage.Self.SchemaVersion!=CurrentStatisticsSchema
            ?ExistingState(settings):null);
        if(Prune(usage,topology) | transitioned)
        {
            // Permit publishing the sanitized self snapshot promptly on the next cycle.
            usage.LastPublishedUtc=null;
            Save(settings,usage);
        }
    }
    private static V2OperationTotals Difference(V2OperationTotals now,V2OperationTotals? old)
    {
        old??=new();
        var oldExact=old.LatencyMilliseconds??new Dictionary<long,long>();
        var exact=new Dictionary<long,long>();
        if(now.LatencyMilliseconds is { } currentExact)
            foreach(var (duration,count) in currentExact)
            {
                oldExact.TryGetValue(duration,out var previous);
                if(count>previous)exact[duration]=count-previous;
            }
        return new V2OperationTotals{Get=Math.Max(0,now.Get-old.Get),Put=Math.Max(0,now.Put-old.Put),
            Probe=Math.Max(0,now.Probe-old.Probe),List=Math.Max(0,now.List-old.List),
            Mkdir=Math.Max(0,now.Mkdir-old.Mkdir),Delete=Math.Max(0,now.Delete-old.Delete),
            DownloadBytes=Math.Max(0,now.DownloadBytes-old.DownloadBytes),
            UploadBytes=Math.Max(0,now.UploadBytes-old.UploadBytes),
            ElapsedMilliseconds=Math.Max(0,now.ElapsedMilliseconds-old.ElapsedMilliseconds),
            Failed=Math.Max(0,now.Failed-old.Failed),
            SuccessfulMilliseconds=Math.Max(0,now.SuccessfulMilliseconds-old.SuccessfulMilliseconds),
            DownloadMilliseconds=Math.Max(0,now.DownloadMilliseconds-old.DownloadMilliseconds),
            UploadMilliseconds=Math.Max(0,now.UploadMilliseconds-old.UploadMilliseconds),
            LatencyMilliseconds=exact};
    }
    // Combine the latest engine totals with our local snapshot without publishing
    // anything or modifying usage.json. This is ONLY for displaying this PC's live
    // figures; peers always retain the last stats/<id>.json received from clouds.
    internal static UsageLocal LoadForDisplay(AppOptions settings,AppState? currentState)
    {
        var usage=Load(settings);
        if(currentState is not null &&
           (usage.Self.InstallationId.Length==0 ||
            usage.Self.InstallationId==currentState.InstallationId))
            ApplyLocalTotals(usage,settings,currentState);
        var topology=TopologyCoordinator.Load(settings);
        if(topology is not null)Prune(usage,topology);
        return usage;
    }
    // Both durable accounting and the live UI use exactly the same delta logic.
    // No remote requests are made, and the view's overlay is never written to disk.
    private static void ApplyLocalTotals(UsageLocal usage,AppOptions settings,AppState state)
    {
        var self=usage.Self;
        self.InstallationId=state.InstallationId;
        self.ComputerName=PlatformServices.Current.Paths.MachineName;
        var now=DateTimeOffset.Now;
        var day=now.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
        var cutoff=now.AddDays(-62).ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
        var changed=false;
        foreach(var cloud in settings.Clouds)
        {
            if(!state.V2Totals.TryGetValue(cloud.Name,out var absolute))continue;
            usage.LastEngineTotals.TryGetValue(cloud.Name,out var previous);
            var delta=Difference(absolute,previous);
            usage.LastEngineTotals[cloud.Name]=absolute.Copy();
            if(delta.Get+delta.Put+delta.Probe+delta.List+delta.Mkdir+delta.Delete==0 &&
               delta.DownloadBytes==0 && delta.UploadBytes==0)continue;
            if(!self.Clouds.TryGetValue(cloud.CloudId,out var item))
                self.Clouds[cloud.CloudId]=item=new UsageCloud{CloudId=cloud.CloudId,Name=cloud.Name};
            item.Name=cloud.Name;
            item.Total.Add(delta);
            if(!item.Days.TryGetValue(day,out var daily))item.Days[day]=daily=new();
            daily.Add(delta);
            foreach(var key in item.Days.Keys.Where(k=>string.CompareOrdinal(k,cutoff)<0).ToArray())
                item.Days.Remove(key);
            changed=true;
        }
        if(changed)self.UpdatedUtc=DateTimeOffset.UtcNow;
    }
    internal static void Record(AppOptions settings,AppState state)
    {
        var usage=Load(settings);
        ApplyLocalTotals(usage,settings,state);
        Save(settings,usage);
    }
    internal static V2OperationTotals Period(UsageCloud cloud,string period)
    {
        if(period=="all")return cloud.Total.Copy();
        var now=DateTime.Today;
        var start=period=="week"?now.AddDays(-((int)now.DayOfWeek+6)%7):new DateTime(now.Year,now.Month,1);
        var total=new V2OperationTotals();
        foreach(var (date,value) in cloud.Days)
            if(DateTime.TryParseExact(date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,out var d) && d>=start && d<=now)total.Add(value);
        return total;
    }
    internal static IEnumerable<UsageSnapshot> All(UsageLocal usage)
    {
        yield return usage.Self;
        foreach(var other in usage.Peers.Values)
            if(other.InstallationId!=usage.Self.InstallationId)yield return other;
    }
}

internal static class UsageExchange
{
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"kps-usage-"+Guid.NewGuid().ToString("N")+".tmp");
    // Preparation and result application are short, serialized operations.
    // Network I/O runs outside EngineHost.Gate so a slow statistics provider
    // can never stall an independent KDBX scan.
    internal static Func<CancellationToken,Task<Action>>? CreateBackgroundJob(AppOptions cfg,SyncLog log)
    {
        var usage=UsageStatistics.Load(cfg);
        if(usage.LastPublishedUtc is not null &&
           DateTimeOffset.UtcNow-usage.LastPublishedUtc<TimeSpan.FromMinutes(cfg.StatsPublishMinutes))
            return null;
        var state=SyncStorage.LoadState(cfg.StateFile);
        if(state.TopologyDisabled)return null;
        UsageStatistics.Record(cfg,state);
        usage=UsageStatistics.Load(cfg);
        var self=usage.Self;
        var topology=TopologyCoordinator.Load(cfg);
        var peerIds=(topology?.Computers.Where(c=>!c.Purged).Select(c=>c.Id)??Array.Empty<string>())
            .Where(id=>id!=state.InstallationId).Distinct(StringComparer.Ordinal).ToArray();
        var refreshIds=peerIds.Where(id=>!usage.Peers.TryGetValue(id,out var existing) ||
            existing.UpdatedUtc<=DateTimeOffset.UtcNow.AddMinutes(-cfg.StatsPublishMinutes/2.0))
            .ToArray();
        var installationId=state.InstallationId;
        var updated=DateTimeOffset.UtcNow;

        return async ct=>
        {
            var tempfile=Temp();
            try
            {
                self.UpdatedUtc=updated;
                SyncStorage.SaveJson(tempfile,self,UsageJson.Default.UsageSnapshot);
                var results=await ParallelCloudRunner.RunAsync(cfg.Clouds,async (cloud,token)=>
                {
                    var peers=new List<UsageSnapshot>();
                    var counts=new V2OperationTotals();
                    if(!TopologyCoordinator.IsAuthorized(cloud))
                        return (Cloud:cloud,Published:false,Peers:peers,Counts:counts,Error:(Exception?)null);
                    using var budget=CloudDeadline.Start(cloud,token,5);
                    try
                    {
                        using var store=new V2Engine(cfg,log).CreateStore(cloud);
                        var directory=cloud.RemoteDirectory+"/stats";
                        await store.EnsureDirectoryAsync(directory,budget.Token);
                        await store.UploadAsync(tempfile,directory+"/"+installationId+".json",
                            WriteMode.Replace,budget.Token);
                        counts.Put++;counts.Mkdir++;counts.UploadBytes+=new FileInfo(tempfile).Length;
                        foreach(var id in refreshIds)
                        {
                            var remote=Temp();
                            try
                            {
                                counts.Get++;
                                if(!await store.DownloadAsync(directory+"/"+id+".json",remote,budget.Token))continue;
                                counts.DownloadBytes+=new FileInfo(remote).Length;
                                var peer=JsonSerializer.Deserialize(File.ReadAllText(remote),
                                    UsageJson.Default.UsageSnapshot);
                                if(peer is {SchemaVersion:2} && peer.InstallationId==id)peers.Add(peer);
                            }
                            finally{if(File.Exists(remote))File.Delete(remote);}
                        }
                        return (Cloud:cloud,Published:true,Peers:peers,Counts:counts,Error:(Exception?)null);
                    }
                    catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                    catch(OperationCanceledException) when(budget.IsCancellationRequested)
                    {return (Cloud:cloud,Published:counts.Put>0,Peers:peers,Counts:counts,
                        Error:(Exception?)new TimeoutException("Statistics exchange timed out"));}
                    catch(Exception ex)
                    {return (Cloud:cloud,Published:counts.Put>0,Peers:peers,Counts:counts,Error:(Exception?)ex);}
                },ct);

                // The delegate MUST be invoked under the host Gate. Reload
                // current state rather than overwriting results committed by
                // KDBX workers while statistics were in flight.
                return (Action)(()=>
                {
                    var latestState=SyncStorage.LoadState(cfg.StateFile);
                    var latestUsage=UsageStatistics.Load(cfg);
                    bool published=false;
                    foreach(var result in results)
                    {
                        if(result.Error is not null)
                            log.Warn("Stats exchange "+result.Cloud.Name+": "+result.Error.Message);
                        published|=result.Published;
                        if(!latestState.V2Totals.TryGetValue(result.Cloud.Name,out var total))
                            latestState.V2Totals[result.Cloud.Name]=total=new V2OperationTotals();
                        total.Add(result.Counts);
                        foreach(var peer in result.Peers)
                            if(!latestUsage.Peers.TryGetValue(peer.InstallationId,out var old) ||
                               peer.UpdatedUtc>old.UpdatedUtc)
                                latestUsage.Peers[peer.InstallationId]=peer;
                    }
                    var finishedUtc=DateTimeOffset.UtcNow;
                    if(published && (latestUsage.LastPublishedUtc is null ||
                        finishedUtc>latestUsage.LastPublishedUtc.Value))
                        latestUsage.LastPublishedUtc=finishedUtc;
                    if(TopologyCoordinator.Load(cfg) is { } currentTopology)
                        UsageStatistics.Prune(latestUsage,currentTopology);
                    UsageStatistics.Save(cfg,latestUsage);
                    SyncStorage.SaveState(cfg.StateFile,latestState);
                });
            }
            finally{if(File.Exists(tempfile))File.Delete(tempfile);}
        };
    }

    internal static async Task RunAsync(AppOptions cfg,SyncLog log,CancellationToken ct)
    {
        var usage=UsageStatistics.Load(cfg);
        if(usage.LastPublishedUtc is not null &&
           DateTimeOffset.UtcNow-usage.LastPublishedUtc<TimeSpan.FromMinutes(cfg.StatsPublishMinutes))return;
        var state=SyncStorage.LoadState(cfg.StateFile);
        if(state.TopologyDisabled)return;
        // Include the just-finished cloud polling/sync cycle in this publication.
        // The hourly publication is independent of what the local UI displays.
        UsageStatistics.Record(cfg,state);
        usage=UsageStatistics.Load(cfg);
        var doc=TopologyCoordinator.Load(cfg);
        var ids=(doc?.Computers.Where(d=>!d.Purged).Select(d=>d.Id)??Array.Empty<string>())
            .Where(id=>id!=state.InstallationId).Distinct(StringComparer.Ordinal).ToArray();
        var tempfile=Temp();
        try
        {
            // This published snapshot is not part of the KDBX synchronization protocol.
            usage.Self.UpdatedUtc=DateTimeOffset.UtcNow;
            SyncStorage.SaveJson(tempfile,usage.Self,UsageJson.Default.UsageSnapshot);
            // Fetch and publish each replica concurrently, but merge peer snapshots
            // and disk totals only on the coordinator thread after completion.
            var freshPeerIds=ids.Where(id=>!usage.Peers.TryGetValue(id,out var existing) ||
                existing.UpdatedUtc<=DateTimeOffset.UtcNow.AddMinutes(-cfg.StatsPublishMinutes/2.0))
                .ToArray();
            var results=await ParallelCloudRunner.RunAsync(cfg.Clouds,async (cloud,token)=>
            {
                var peers=new List<UsageSnapshot>();
                var counts=new V2OperationTotals();
                if(!TopologyCoordinator.IsAuthorized(cloud))
                    return (Cloud:cloud,Published:false,Peers:peers,Counts:counts,Error:(Exception?)null);
                using var budget=CloudDeadline.Start(cloud,token,5);
                try
                {
                    using var store=new V2Engine(cfg,log).CreateStore(cloud);
                    var directory=cloud.RemoteDirectory+"/stats";
                    await store.EnsureDirectoryAsync(directory,budget.Token);
                    await store.UploadAsync(tempfile,directory+"/"+state.InstallationId+".json",WriteMode.Replace,budget.Token);
                    counts.Put++;counts.Mkdir++;counts.UploadBytes+=new FileInfo(tempfile).Length;
                    foreach(var id in freshPeerIds)
                    {
                        var remote=Temp();
                        try
                        {
                            counts.Get++;
                            if(!await store.DownloadAsync(directory+"/"+id+".json",remote,budget.Token))continue;
                            counts.DownloadBytes+=new FileInfo(remote).Length;
                            var peer=JsonSerializer.Deserialize(File.ReadAllText(remote),UsageJson.Default.UsageSnapshot);
                            if(peer is {SchemaVersion:2} && peer.InstallationId==id)
                                peers.Add(peer);
                        }
                        finally{if(File.Exists(remote))File.Delete(remote);}
                    }
                    return (Cloud:cloud,Published:true,Peers:peers,Counts:counts,Error:(Exception?)null);
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                catch(OperationCanceledException) when(budget.IsCancellationRequested)
                {return (Cloud:cloud,Published:counts.Put>0,Peers:peers,Counts:counts,
                    Error:(Exception?)new TimeoutException("Statistics exchange timed out"));}
                catch(Exception e)
                {
                    // A successful PUT followed by a failed peer GET still counts as
                    // published; the next cycle can retry missing peer snapshots.
                    return (Cloud:cloud,Published:counts.Put>0,Peers:peers,Counts:counts,Error:(Exception?)e);
                }
            },ct);
            bool published=false;
            foreach(var result in results)
            {
                if(result.Error is not null)
                    log.Warn("Stats exchange "+result.Cloud.Name+": "+result.Error.Message);
                published|=result.Published;
                if(!state.V2Totals.TryGetValue(result.Cloud.Name,out var totals))
                    state.V2Totals[result.Cloud.Name]=totals=new V2OperationTotals();
                totals.Add(result.Counts);
                foreach(var peer in result.Peers)
                    if(!usage.Peers.TryGetValue(peer.InstallationId,out var last) ||
                       peer.UpdatedUtc>last.UpdatedUtc)
                        usage.Peers[peer.InstallationId]=peer;
            }
            if(published)usage.LastPublishedUtc=DateTimeOffset.UtcNow;
            if(doc is not null)UsageStatistics.Prune(usage,doc);
            UsageStatistics.Save(cfg,usage);
            SyncStorage.SaveState(cfg.StateFile,state);
        }
        finally{if(File.Exists(tempfile))File.Delete(tempfile);}
    }
}
