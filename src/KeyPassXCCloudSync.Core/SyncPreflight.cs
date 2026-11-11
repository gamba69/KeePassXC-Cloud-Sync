using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KeyPassXCCloudSync;

// Explicit, read-only startup examination. This is NOT a distributed lock: any
// result describes the instant of inspection, never a guarantee about the future.
// Unknown ancestry inside a verified replica is never displayed as safe.
// Unreachable replicas are explicitly reported as unverified, not conflict-free.
internal enum PreflightOutcome { NoConflict, Conflict, Undetermined }
internal sealed record PreflightReport(PreflightOutcome Outcome,string Detail,string Snapshot,
    int VerifiedClouds=0,int UnavailableClouds=0);

// Only a demonstrably unavailable transport may be excluded from an activation
// preflight. Invalid identities, version graphs, revisions or topology documents
// remain fatal; they are not an excuse to skip a replica and publish elsewhere.
internal static class PreflightAvailability
{
    internal static bool IsTemporary(CloudOptions cloud,Exception error)
    {
        if(error is TimeoutException or System.Net.Sockets.SocketException)return true;
        if(error is HttpRequestException http)
        {
            if(http.StatusCode is null)return true; // DNS, TLS or connection failure.
            var status=(int)http.StatusCode.Value;
            return status is 401 or 403 or 408 or 429 || status>=500 && status<=599;
        }
        // The official Proton CLI reports failed processes through the Windows
        // process adapter as IOException. Other filesystem IO errors are not
        // automatically interpreted as a remote outage.
        return cloud.Provider=="ProtonDrive" && error is IOException &&
            error.Message.StartsWith("Proton Drive CLI",StringComparison.Ordinal);
    }
}
internal static class SyncPreflight
{
    private sealed record Node(string[] Parents,string Sha);
    private sealed record CloudInspection(
        CloudOptions Cloud,TopologyDocument? RemoteTopology,string? TopologyToken,
        string Sha,string? WorkingRevision,List<VersionInfo> Versions,
        List<V2DevicePointer> Pointers,List<string> Fingerprints,
        List<string> Lines,HashSet<string> Announced,string? Unavailable=null)
    {
        internal static CloudInspection Skipped(CloudOptions cloud,string reason)=>
            new(cloud,null,null,"",null,[],[],[],[],[],reason);
    }
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"kps-preflight-"+Guid.NewGuid().ToString("N")+".tmp");
    private static async Task<T?> ReadJson<T>(IRemoteStore store,string key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,CancellationToken ct) where T:class
    {
        var path=Temp();
        try
        {
            if(!await store.DownloadAsync(key,path,ct))return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path),type)
                ??throw new SyncSafetyException("Пустой удалённый JSON: "+key);
        }
        finally{if(File.Exists(path))File.Delete(path);}
    }
    private static bool? IsAncestor(string older,string newer,Dictionary<string,Node> nodes)
    {
        if(older==newer)return true;
        var todo=new Stack<string>();todo.Push(newer);
        var seen=new HashSet<string>(StringComparer.Ordinal);
        bool missing=false;
        while(todo.TryPop(out var current))
        {
            if(!seen.Add(current))continue;
            if(!nodes.TryGetValue(current,out var node)){missing=true;continue;}
            foreach(var parent in node.Parents)
            {
                if(parent==older)return true;
                todo.Push(parent);
            }
        }
        return missing?(bool?)null:false;
    }
    internal static async Task<PreflightReport> CheckAsync(AppOptions cfg,CancellationToken ct)
    {
        var lines=new List<string>();
        string localHash="";
        try
        {
            if(!File.Exists(cfg.LocalFile))lines.Add("Локальная KDBX отсутствует: потребуется загрузка из облака.");
            else
            {
                KdbxInspector.VerifyPublicHeader(cfg.LocalFile);
                var initial=new FileInfo(cfg.LocalFile);
                var beforeLen=initial.Length;var beforeStamp=initial.LastWriteTimeUtc.Ticks;
                localHash=Hashes.Sha256(cfg.LocalFile);
                var final=new FileInfo(cfg.LocalFile);
                if(final.Length!=beforeLen || final.LastWriteTimeUtc.Ticks!=beforeStamp)
                    throw new SyncSafetyException("Локальная KDBX менялась во время проверки.");
                lines.Add("Локальная KDBX найдена; контрольная сумма проверена.");
            }
            if(cfg.Clouds.Count==0)throw new SyncSafetyException("Не настроено ни одного облака.");
            var state=SyncStorage.LoadState(cfg.StateFile);
            if(state.TopologyDisabled)throw new SyncSafetyException("Этот компьютер исключён из топологии.");
            if(File.Exists(Path.Combine(cfg.DataDirectory,"v2-local-install.json")))
                throw new SyncSafetyException("Найден незавершённый локальный журнал замены KDBX. Требуется восстановление.");
            var nodes=new Dictionary<string,Node>(StringComparer.Ordinal);
            var localHistoryIds=new HashSet<string>(StringComparer.Ordinal);
            var seenVaultIds=new HashSet<string>(StringComparer.Ordinal);
            var knownByHash=new Dictionary<string,string>(StringComparer.Ordinal);
            var candidates=new List<string>();
            var fingerprint=new List<string>{cfg.LocalFile,cfg.StateFile,localHash,state.LocalVersionId??"",state.VaultId??""};
            void AddVersion(VersionInfo v)
            {
                if(!Guid.TryParseExact(v.VersionId,"N",out _) || !Hashes.Valid(v.Sha256) ||
                    v.Size<1 || !Guid.TryParseExact(v.VaultId,"N",out _) ||
                    v.Parents.Contains(v.VersionId) ||
                    v.Parents.Distinct(StringComparer.Ordinal).Count()!=v.Parents.Length ||
                    v.BackupName!=v.VersionId+".kdbx")
                    throw new SyncSafetyException("Некорректная история версий: "+v.VersionId);
                seenVaultIds.Add(v.VaultId);
                if(state.VaultId is not null && v.VaultId!=state.VaultId)
                    throw new SyncSafetyException("Чужая база (VaultId) в истории версий.");
                if(nodes.TryGetValue(v.VersionId,out var existing))
                {
                    if(existing.Sha!=v.Sha256 || !existing.Parents.SequenceEqual(v.Parents))
                        throw new SyncSafetyException("Несогласованные метаданные версии: "+v.VersionId);
                    return;
                }
                nodes.Add(v.VersionId,new Node(v.Parents,v.Sha256));
            }
            var history=Path.Combine(cfg.DataDirectory,"history");
            if(Directory.Exists(history))
                foreach(var file in Directory.EnumerateFiles(history,"*.json"))
                {
                    var v=JsonSerializer.Deserialize(File.ReadAllText(file),SyncJson.Default.VersionInfo)
                        ??throw new SyncSafetyException("Повреждена локальная история: "+file);
                    if(Path.GetFileNameWithoutExtension(file)!=v.VersionId)
                        throw new SyncSafetyException("Имя локального файла истории не совпадает с ID.");
                    AddVersion(v);
                    localHistoryIds.Add(v.VersionId);
                    fingerprint.Add("local-history|"+v.VersionId+"|"+
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(v,SyncJson.Default.VersionInfo)))));
                }
            var topology=TopologyCoordinator.Load(cfg);
            if(topology is not null)
                fingerprint.Add("local-topology|"+Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(topology,TopologyJson.Default.TopologyDocument)))));
            var allowed=topology?.Computers.Where(c=>c.Active).Select(c=>c.Id)
                .ToHashSet(StringComparer.Ordinal);
            string? remoteTopologyId=null;
            string? remoteTopologyChange=null;
            bool missingRemoteTopology=false;
            bool foundRemoteTopology=false;
            var rawWorking=new List<(CloudOptions Cloud,string Sha,string? Revision)>();
            var announcedByCloud=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
            var pointers=new List<V2DevicePointer>();
            async Task<CloudInspection> InspectCloud(CloudOptions cloud,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                using var budget=CloudDeadline.Start(cloud,token,6);
                try
                {
                var cloudToken=budget.Token;
                var engine=new V2Engine(cfg,new SyncLog(cfg));
                var cloudFingerprints=new List<string>();
                var cloudLines=new List<string>();
                var cloudVersions=new List<VersionInfo>();
                var cloudPointers=new List<V2DevicePointer>();
                var announced=new HashSet<string>(StringComparer.Ordinal);
                TopologyDocument? remoteDocument=null;
                string cloudSha="";
                string? cloudWorkingRevision=null;
                if(!TopologyCoordinator.IsAuthorized(cloud))
                    return CloudInspection.Skipped(cloud,"Требуется авторизация");
                using var store=engine.CreateStore(cloud);
                var prefix=cloud.RemoteDirectory;

                var topologyToken=await V2Engine.RevisionAsync(store,prefix+"/topology.json",cloudToken);
                if(topologyToken?.StartsWith("unknown-",StringComparison.Ordinal)==true)
                    throw new SyncSafetyException("Невозможно однозначно проверить топологию в «"+cloud.Name+"».");
                cloudFingerprints.Add("topology-revision|"+cloud.CloudId+"|"+topologyToken);
                if(topologyToken is not null)
                {
                    var currentTopology=await ReadJson(store,prefix+"/topology.json",
                        TopologyJson.Default.TopologyDocument,cloudToken)
                        ??throw new SyncSafetyException("Исчезла топология в облаке «"+cloud.Name+"».");
                    if(currentTopology.SchemaVersion!=1 ||
                       !Guid.TryParseExact(currentTopology.TopologyId,"N",out _) ||
                       !Guid.TryParseExact(currentTopology.ChangeId,"N",out _) ||
                       currentTopology.Revision<1)
                        throw new SyncSafetyException("Повреждена топология в облаке «"+cloud.Name+"».");
                    remoteDocument=currentTopology;
                    if(topology is not null &&
                       (currentTopology.TopologyId!=topology.TopologyId ||
                        currentTopology.ChangeId!=topology.ChangeId))
                        throw new SyncSafetyException("Локальная топология отстаёт от «"+cloud.Name+
                            "». Дождитесь обновления в холостом режиме.");
                    if(await V2Engine.RevisionAsync(store,prefix+"/topology.json",cloudToken)!=topologyToken)
                        throw new SyncSafetyException("Топология облака «"+cloud.Name+
                            "» изменилась во время проверки.");
                    cloudFingerprints.Add("topology-content|"+cloud.CloudId+"|"+
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(currentTopology,TopologyJson.Default.TopologyDocument)))));
                }
                if(!state.V2Cursors.TryGetValue(cloud.CloudId,out var savedCursor) ||
                   savedCursor.LastDeviceDiscoveryUtc is null)
                {
                    // Refuse any incompatible remote directory layout.
                    var incompatible=Temp();
                    try
                    {
                        if(await store.DownloadAsync(prefix+"/state/database.json",incompatible,cloudToken))
                            throw new SyncSafetyException("В облаке «"+cloud.Name+"» находится устаревший несовместимый формат.");
                    }
                    finally{if(File.Exists(incompatible))File.Delete(incompatible);}
                }
                var working=prefix+"/database.kdbx";
                var before=await V2Engine.RevisionAsync(store,working,cloudToken);
                var workFile=Temp();
                string sha="";
                try
                {
                    var present=await store.DownloadAsync(working,workFile,cloudToken);
                    if(present)
                    {
                        KdbxInspector.VerifyPublicHeader(workFile);
                        sha=Hashes.Sha256(workFile);
                    }
                    if(present!=(before is not null))
                        throw new SyncSafetyException("Файл в облаке «"+cloud.Name+"» изменился во время проверки.");
                }
                finally{if(File.Exists(workFile))File.Delete(workFile);}
                var after=await V2Engine.RevisionAsync(store,working,cloudToken);
                if(before!=after || (before?.StartsWith("unknown-",StringComparison.Ordinal)==true))
                    throw new SyncSafetyException("Облако «"+cloud.Name+"» изменилось во время проверки или не сообщает ревизию.");
                cloudFingerprints.Add(cloud.CloudId+"|"+cloud.Provider+"|"+cloud.RemoteDirectory+"|"+after+"|"+sha);
                cloudSha=sha;
                cloudWorkingRevision=after;
                cloudLines.Add(cloud.Name+": "+(sha.Length>0?"KDBX найдена, SHA-256 прочитан.":"рабочей KDBX пока нет."));

                var versionListing=await store.ListAsync(prefix+"/versions",cloudToken);
                foreach(var file in versionListing.OrderBy(x=>x.Path,StringComparer.Ordinal))
                {
                    if(!file.Path.EndsWith(".json",StringComparison.OrdinalIgnoreCase))continue;
                    var basename=Path.GetFileNameWithoutExtension(file.Path);
                    if(!Guid.TryParseExact(basename,"N",out _))continue;
                    var v=await ReadJson(store,file.Path,SyncJson.Default.VersionInfo,cloudToken)
                        ??throw new SyncSafetyException("Исчезла метаинформация версии в «"+cloud.Name+"».");
                    if(v.VersionId!=basename)throw new SyncSafetyException("Несовпадение ID метаданных.");
                    cloudVersions.Add(v);
                    cloudFingerprints.Add(cloud.CloudId+"|metadata|"+v.VersionId+"|"+
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(v,SyncJson.Default.VersionInfo)))));
                }
                var files=await store.ListAsync(prefix+"/devices",cloudToken);
                foreach(var file in files.OrderBy(x=>x.Path,StringComparer.Ordinal))
                {
                    if(!file.Path.EndsWith(".json",StringComparison.OrdinalIgnoreCase))continue;
                    var id=Path.GetFileNameWithoutExtension(file.Path);
                    if(!Guid.TryParseExact(id,"N",out _) || allowed is not null && !allowed.Contains(id))continue;
                    var pointerRevision=await V2Engine.RevisionAsync(store,file.Path,cloudToken);
                    if(pointerRevision is null || pointerRevision.StartsWith("unknown-",StringComparison.Ordinal))
                        throw new SyncSafetyException("Нельзя надёжно определить ревизию указателя компьютера в «"+cloud.Name+"».");
                    var pointer=await ReadJson(store,file.Path,SyncJson.Default.V2DevicePointer,cloudToken)
                        ??throw new SyncSafetyException("Исчез указатель компьютера в «"+cloud.Name+"».");
                    if(pointer.SchemaVersion!=2 || pointer.InstallationId!=id ||
                        !Guid.TryParseExact(pointer.VaultId,"N",out _) ||
                        !Guid.TryParseExact(pointer.VersionId,"N",out _))
                        throw new SyncSafetyException("Некорректный указатель компьютера в «"+cloud.Name+"».");
                    if(state.VaultId is not null && state.VaultId!=pointer.VaultId)
                        throw new SyncSafetyException("В облаке обнаружена другая база (VaultId).");
                    var pointerAfter=await V2Engine.RevisionAsync(store,file.Path,cloudToken);
                    if(pointerAfter!=pointerRevision)
                        throw new SyncSafetyException("Указатель компьютера изменился во время проверки «"+cloud.Name+"».");
                    cloudPointers.Add(pointer);
                    if(savedCursor is null || !savedCursor.DeviceRevisions.TryGetValue(id,out var priorRev) ||
                        priorRev!=pointerRevision)
                        announced.Add(pointer.VersionId);
                    cloudFingerprints.Add(cloud.CloudId+"|"+pointer.InstallationId+"|"+pointer.VersionId+"|"+
                        pointer.VaultId+"|"+pointer.TopologyRevision+"|"+pointerRevision);
                }
                return new CloudInspection(cloud,remoteDocument,topologyToken,cloudSha,cloudWorkingRevision,
                    cloudVersions,cloudPointers,cloudFingerprints,cloudLines,announced);
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                catch(OperationCanceledException) when(budget.IsCancellationRequested)
                {return CloudInspection.Skipped(cloud,"Тайм-аут проверки");}
                catch(Exception error) when(PreflightAvailability.IsTemporary(cloud,error))
                {return CloudInspection.Skipped(cloud,error.GetType().Name+": "+error.Message);}
            }
            var inspected=await ParallelCloudRunner.RunAsync(cfg.Clouds,InspectCloud,ct);
            int verifiedClouds=0;
            var skippedClouds=new List<string>();
            foreach(var observation in inspected)
            {
                if(observation.Unavailable is not null)
                {
                    skippedClouds.Add(observation.Cloud.Name+": "+observation.Unavailable);
                    // Do not hash volatile network exception text into the
                    // double-check snapshot. Presence/absence is the safety
                    // boundary; an actual recovery changes the snapshot.
                    fingerprint.Add("unverified-cloud|"+observation.Cloud.CloudId);
                    continue;
                }
                verifiedClouds++;
                if(observation.TopologyToken is null)missingRemoteTopology=true;
                else foundRemoteTopology=true;
                if(observation.RemoteTopology is not null)
                {
                    if(remoteTopologyId is not null &&
                       (observation.RemoteTopology.TopologyId!=remoteTopologyId ||
                        observation.RemoteTopology.ChangeId!=remoteTopologyChange))
                        throw new SyncSafetyException("Облака имеют несовпадающие версии topology.json. " +
                            "Нельзя надёжно начать синхронизацию KDBX.");
                    remoteTopologyId=observation.RemoteTopology.TopologyId;
                    remoteTopologyChange=observation.RemoteTopology.ChangeId;
                }
                fingerprint.AddRange(observation.Fingerprints);
                lines.AddRange(observation.Lines);
                rawWorking.Add((observation.Cloud,observation.Sha,observation.WorkingRevision));
                announcedByCloud[observation.Cloud.CloudId]=observation.Announced;
                foreach(var version in observation.Versions)AddVersion(version);
                foreach(var pointer in observation.Pointers)
                {
                    seenVaultIds.Add(pointer.VaultId);
                    pointers.Add(pointer);
                }
            }
            if(verifiedClouds==0)
                throw new SyncSafetyException("Ни одно облако не удалось проверить. " +
                    "Включение без подтверждённой реплики запрещено. " + string.Join("; ",skippedClouds));
            if((foundRemoteTopology && missingRemoteTopology) ||
               (topology is not null && missingRemoteTopology))
                throw new SyncSafetyException("Не во всех облаках доступна согласованная topology.json. " +
                    "Дождитесь обмена топологией, прежде чем включать KDBX.");
            if(seenVaultIds.Count>1)
                throw new SyncSafetyException("В подключениях обнаружены разные VaultId: смешивание баз запрещено.");
            // The real engine always loads the full LOCAL history, including unresolved
            // independent heads. It would be unsafe to ignore one just because no
            // current device pointer names it.
            var localParents=localHistoryIds.SelectMany(id=>nodes[id].Parents)
                .ToHashSet(StringComparer.Ordinal);
            foreach(var localId in localHistoryIds)
                if(!localParents.Contains(localId))candidates.Add(localId);
            foreach(var pointer in pointers)
            {
                if(!nodes.ContainsKey(pointer.VersionId))
                    throw new SyncSafetyException("Версия указателя компьютера недоступна в истории: "+pointer.VersionId);
                candidates.Add(pointer.VersionId);
            }
            // The actual sync engine loads LOCAL metadata plus metadata reachable
            // from active device pointers. An orphan file in versions/ must not
            // falsely prove that a different working KDBX is a safe descendant.
            var reachable=new HashSet<string>(StringComparer.Ordinal);
            var visit=new Stack<string>(localHistoryIds.Concat(pointers.Select(x=>x.VersionId)));
            while(visit.TryPop(out var versionId))
            {
                if(!reachable.Add(versionId) || !nodes.TryGetValue(versionId,out var version))continue;
                foreach(var parent in version.Parents)visit.Push(parent);
            }
            foreach(var id in reachable.OrderBy(x=>x,StringComparer.Ordinal))
                if(nodes.TryGetValue(id,out var version))knownByHash.TryAdd(version.Sha,id);
            foreach(var (cloud,sha,revision) in rawWorking)
            {
                state.V2Cursors.TryGetValue(cloud.CloudId,out var cursor);
                if(sha.Length==0)
                {
                    if(cursor is not null && cursor.WorkingRevision==revision && cursor.WorkingVersionId is not null)
                        throw new SyncSafetyException("Несовпадение рабочего файла и локального курсора облака «"+cloud.Name+"».");
                    continue;
                }
                // With unchanged remote revision the sync engine trusts its local
                // cursor, not a fresh hash-to-ID guess. An inconsistent cursor is
                // UNKNOWN, never an accidentally safe result.
                if(cursor is not null && cursor.WorkingRevision==revision)
                {
                    if(cursor.WorkingVersionId is null ||
                        !nodes.TryGetValue(cursor.WorkingVersionId,out var current) || current.Sha!=sha)
                        throw new SyncSafetyException("Не удалось подтвердить курсор неизменённого облака «"+cloud.Name+"».");
                    candidates.Add(cursor.WorkingVersionId);
                    continue;
                }
                var hasKnown=knownByHash.TryGetValue(sha,out var id);
                var independent= !hasKnown || (cursor?.WorkingRevision is not null &&
                    cursor.WorkingVersionId!=id && !announcedByCloud[cloud.CloudId].Contains(id!));
                if(independent)
                {
                    // External direct edits / reversions create a NEW child of
                    // the last cloud version, even if its bytes match an ancestor.
                    id="external:"+cloud.CloudId;
                    var parent=cursor?.WorkingVersionId;
                    nodes[id]=new Node(parent is null?[]:[parent],sha);
                    knownByHash.TryAdd(sha,id);
                }
                candidates.Add(id!);
            }
            if(localHash.Length>0)
            {
                if(state.LocalVersionId is not null && state.LocalSha256==localHash)
                {
                    if(!nodes.TryGetValue(state.LocalVersionId,out var localKnown) || localKnown.Sha!=localHash)
                        throw new SyncSafetyException("Нет подтверждённой локальной истории последней версии KDBX.");
                    candidates.Add(state.LocalVersionId);
                }
                else
                {
                    // An edited KDBX is a NEW child of its last local version,
                    // even when it reuses the exact bytes of an older revision.
                    // A brand-new installation may adopt one matching remote node.
                    var matches=nodes.Where(x=>(reachable.Contains(x.Key) || x.Key.StartsWith("external:",StringComparison.Ordinal))
                        && x.Value.Sha==localHash).Select(x=>x.Key).ToArray();
                    if(state.LocalVersionId is null && matches.Length==1)
                        candidates.Add(matches[0]);
                    else
                    {
                        const string localId="local:pending";
                        var parent=state.LocalVersionId;
                        nodes[localId]=new Node(parent is null?[]:[parent],localHash);
                        candidates.Add(localId);
                    }
                }
            }
            if(candidates.Count==0)throw new SyncSafetyException("Ни локальной KDBX, ни доступной версии в облаках нет.");
            var unique=candidates.Distinct(StringComparer.Ordinal).ToArray();
            var tips=new HashSet<string>(unique,StringComparer.Ordinal);
            bool uncertain=false;
            foreach(var a in unique)
                foreach(var b in unique)
                {
                    if(a==b)continue;
                    var result=IsAncestor(a,b,nodes);
                    if(result==true)tips.Remove(a);
                    if(result is null)uncertain=true;
                }
            PreflightOutcome outcome;
            if(tips.Count==1)outcome=PreflightOutcome.NoConflict;
            else if(uncertain)outcome=PreflightOutcome.Undetermined;
            else outcome=PreflightOutcome.Conflict;
            // An outdated or malformed graph must never produce a false green approval.
            if(outcome==PreflightOutcome.NoConflict && uncertain)outcome=PreflightOutcome.Undetermined;
            var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",fingerprint))));
            var headline=outcome switch
            {
                PreflightOutcome.NoConflict=>"КОНФЛИКТА СРЕДИ ПРОВЕРЕННЫХ РЕПЛИК НЕ ОБНАРУЖЕНО: известные активные версии образуют одну цепочку.",
                PreflightOutcome.Conflict=>"КОНФЛИКТ ОБНАРУЖЕН: существуют независимые версии KDBX. Автоматического объединения не будет.",
                _=>"НЕВОЗМОЖНО ОДНОЗНАЧНО ПРОВЕРИТЬ: отсутствует подтверждённая история версий."
            };
            var availability=skippedClouds.Count==0
                ?"Проверены все облака ("+verifiedClouds+")."
                :"ЧАСТИЧНАЯ ПРОВЕРКА: проверено "+verifiedClouds+" из "+cfg.Clouds.Count+
                    ". Непроверенные облака НЕ считаются безопасными и не будут перезаписаны без собственной проверки:\n- "+
                    string.Join("\n- ",skippedClouds)+
                    "\nПосле восстановления необходимо проверить историю, VaultId и топологию каждой реплики. Возможен конфликт.";
            return new PreflightReport(outcome,headline+"\n\n"+availability+"\n\n"+string.Join("\n",lines)+
                "\n\nРезультат действителен только на момент проверки. Перед включением выполняется повторная сверка.",
                digest,verifiedClouds,skippedClouds.Count);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception e)
        {
            return new PreflightReport(PreflightOutcome.Undetermined,
                "НЕ УДАЛОСЬ ПРОВЕРИТЬ ВСЕ РЕПЛИКИ. Синхронизация не будет включена.\n\n"+
                e.Message+"\n\n"+string.Join("\n",lines),"");
        }
    }
}
