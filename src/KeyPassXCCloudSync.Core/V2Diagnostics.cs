namespace KeyPassXCCloudSync;

// v2 diagnostics operate only on isolated local/remote test roots; they never
// modify the production KDBX. All assertions are reported individually.
internal static class V2Diagnostics
{
    internal static Action<string>? Progress;
    private static readonly List<string> ReportLines=new();
    private static void Emit(string s){ReportLines.Add(s);Progress?.Invoke(s);}
    private static void StartReport()=>ReportLines.Clear();
    private static void SaveReport(AppOptions? options,string suite)
    {
        var dataDir=options?.DataDirectory ?? PlatformServices.Current.Paths.DefaultDataDirectory(
            Path.Combine(AppContext.BaseDirectory,"KeyPassXCCloudSync.ini"));
        var target=Path.Combine(dataDir,"test-reports");
        Directory.CreateDirectory(target);
        var name="v2-"+suite+"-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+
            Guid.NewGuid().ToString("N")[..6];
        var entries=ReportLines.ToArray();
        File.WriteAllText(Path.Combine(target,name+".json"),
            System.Text.Json.JsonSerializer.Serialize(entries,StateJsonContext.Default.StringArray));
        var xml=new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("KeePassSyncV2Diagnostics",
            new System.Xml.Linq.XAttribute("suite",suite),
            new System.Xml.Linq.XAttribute("utc",DateTimeOffset.UtcNow.ToString("O")),
            entries.Select(x=>new System.Xml.Linq.XElement("line",x))));
        xml.Save(Path.Combine(target,name+".xml"));
    }
    private static void Assert(bool ok,string message)
    {if(!ok)throw new SyncSafetyException(message);}
    // A test emits only its terminal outcome; failures retain the exact case name.
    // The wrapper prevents an outer suite catch from emitting a duplicate FAIL.
    private sealed class ReportedCheckException : Exception
    {
        internal ReportedCheckException(Exception inner) : base(inner.Message,inner){}
    }
    private static void Check(string name,Action action)
    {
        try
        {
            action();
            Emit("[PASS] "+name);
        }
        catch(Exception e)
        {
            Emit("[FAIL] "+name+": "+e.GetType().Name+": "+e.Message);
            throw new ReportedCheckException(e);
        }
    }
    private static AppOptions Options(AppOptions? production,CloudOptions cloud,string folder,params CloudOptions[] otherClouds)
    {
        var state=Path.Combine(folder,"data","state.json");
        var allClouds=new List<CloudOptions>{cloud};
        allClouds.AddRange(otherClouds);
        var c=new AppOptions {
            ConfigPath=Path.Combine(folder,"test-only.ini"),
            LocalFile=Path.Combine(folder,"vault.kdbx"),StateFile=state,
            DataDirectory=Path.GetDirectoryName(state)!,Clouds=allClouds,
            TrayEnabled=false,SyncEnabled=false,PollSeconds=60,StatsPublishMinutes=60,DebounceMs=0,StableMs=100,RetrySeconds=60,
            NotifyAfterMinutes=10,BackupCopies=20,BackupDays=30,
            BackupOnStartup=false,StartupBackupKeep=30,

            LogLevel="Info",LogSizeMB=5,LogFiles=5
        };
        Directory.CreateDirectory(folder);
        var draft=new SettingsDraft{LocalFile=c.LocalFile,DataDirectory=c.DataDirectory,
            Clouds=allClouds.Select(CloudDraft.From).ToList()};
        File.WriteAllText(c.ConfigPath,SettingsStore.Ini(draft));
        return c;
    }
    private static CloudOptions FakeCloud(string root,bool cas)
    {
        var uri=new Uri(Path.GetFullPath(root)).AbsoluteUri;
        return new CloudOptions("Test",cas?"FileSystemTest":"FileSystemTestNoCas",
            "vault",uri,"","","","","","","",30,CloudId:"Test");
    }
    private static AppState State(AppOptions c)=>SyncStorage.LoadState(c.StateFile);
    private static V2Engine New(AppOptions c)=>new(c,new SyncLog(c));
    // A live integration test must actually exercise the scheduled cloud poll.
    // Immediately after bootstrap the per-cloud timer is still in the future,
    // so a plain RunOnceAsync legitimately skips the transport and emits no metrics.
    // Only change the due time of the isolated test client, never production state.
    private static void MakeTestCloudDue(AppOptions test,CloudOptions cloud)
    {
        var isolatedLive=IntegrationIsolation.IsTestRunRoot(cloud.RemoteDirectory);
        var isolatedLocal=(cloud.Provider is "FileSystemTest" or "FileSystemTestNoCas") &&
            Environment.GetEnvironmentVariable("KPS_ENABLE_TEST_STORE")=="1";
        var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(
            Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!(isolatedLive || isolatedLocal) ||
           !Path.GetFileName(test.ConfigPath).Equals("test-only.ini",StringComparison.Ordinal) ||
           !Path.GetFullPath(test.DataDirectory).StartsWith(
                tempRoot,StringComparison.OrdinalIgnoreCase))
            throw new SyncSafetyException("Refusing to alter non-test cloud poll schedule");
        var state=State(test);
        state.NextCloudPollUtc[cloud.CloudId]=DateTimeOffset.UtcNow.AddSeconds(-1);
        SyncStorage.SaveState(test.StateFile,state);
    }
    private static V2OperationTotals RequireCloudMetrics(V2Engine engine,CloudOptions cloud)
    {
        if(!engine.LastMetrics.TryGetValue(cloud.Name,out var metrics))
            throw new SyncSafetyException("Cloud diagnostic poll did not run for "+cloud.Name+
                "; check the isolated test schedule and cloud authorization");
        return metrics;
    }
    private static V2Engine Sync(AppOptions c)
    {
        var e=New(c);e.RunOnceAsync(CancellationToken.None,true).GetAwaiter().GetResult();return e;
    }
    private static void Write(AppOptions c,string label)=>V2TestFixtures.FileBytes(c.LocalFile,label);
    private static string Working(CloudOptions c)=>new Uri(c.BaseUrl).LocalPath.Replace('\\','/')+
        "/"+c.RemoteDirectory+"/database.kdbx";
    private static int RunSuite(bool safety,AppOptions? options)
    {
        StartReport();
        var prev=Environment.GetEnvironmentVariable("KPS_ENABLE_TEST_STORE");
        Environment.SetEnvironmentVariable("KPS_ENABLE_TEST_STORE","1");
        var baseDir=Path.Combine(Path.GetTempPath(),"kps-v2-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        Emit("KeePassSync tests: v2 "+(safety?"safety and concurrency":"local synchronization and efficiency"));
        Emit("[INFO] Изолированные тестовые каталоги: "+baseDir);
        var passed=0;var failed=0;
        try
        {
            foreach(var hasCas in new[]{true,false})
            {
                var kind=hasCas?"CAS":"no-CAS";
                var cloud=FakeCloud(Path.Combine(baseDir,"remote-"+kind),hasCas);
                var a=Options(null,cloud,Path.Combine(baseDir,"pc-A-"+kind));
                var b=Options(null,cloud,Path.Combine(baseDir,"pc-B-"+kind));
                Directory.CreateDirectory(Path.GetDirectoryName(a.LocalFile)!);
                try
                {
                    Check(kind+": new-vault bootstrap",()=>{
                        Write(a,"v2 BASELINE");Sync(a);
                        Assert(State(a).Result=="SYNCED","Bootstrap must be SYNCED");
                        Assert(File.Exists(Working(cloud)),"Working database not published");
                        Assert(Directory.GetFiles(Path.Combine(new Uri(cloud.BaseUrl).LocalPath,"vault","versions"),"*.kdbx").Length==1,
                            "Immutable backup not published");
                    });passed++;
                    Check(kind+": second computer joins",()=>{
                        Sync(b);
                        Assert(State(b).Result=="SYNCED","Second computer did not join");
                        Assert(Hashes.Sha256(a.LocalFile)==Hashes.Sha256(b.LocalFile),"Different KDBX after join");
                        // The first PC has not yet observed PC-B's newly published device pointer.
                        // Discover and consume that expected one-time metadata update BEFORE
                        // measuring 100 genuinely idle cycles. Otherwise no-CAS transports
                        // correctly perform one small GET and the "zero transfer" assertion
                        // fails for a normal join event, not for idle traffic.
                        var settle=Sync(a);
                        Assert(State(a).Result=="SYNCED","First computer did not settle after peer join");
                        var settleOps=settle.LastMetrics["Test"];
                        Emit($"[NET ] {kind}: peer join settlement = GET {settleOps.Get}, PUT {settleOps.Put}, bytes {settleOps.DownloadBytes+settleOps.UploadBytes}, metadata {settleOps.Probe+settleOps.List}");
                        Assert(settleOps.Put==0,"Peer discovery should not rewrite remote files");
                        if(!hasCas)
                            Assert(State(a).V2Cursors["Test"].DeviceRevisions.ContainsKey(State(b).InstallationId),
                                "Non-CAS transport did not discover new peer pointer");
                    });passed++;
                    if(!safety)
                    {
                        Check(kind+": preflight no-conflict and independent-vault conflict",()=>{
                            var before=Hashes.Sha256(Working(cloud));
                            var local=SyncPreflight.CheckAsync(a,CancellationToken.None).GetAwaiter().GetResult();
                            Assert(local.Outcome==PreflightOutcome.NoConflict,
                                "Identical local and remote KDBX was not classified safe: "+local.Detail);
                            var remoteOnly=Options(null,cloud,Path.Combine(baseDir,"pc-new-"+kind));
                            var noLocal=SyncPreflight.CheckAsync(remoteOnly,CancellationToken.None)
                                .GetAwaiter().GetResult();
                            Assert(noLocal.Outcome==PreflightOutcome.NoConflict,
                                "Fresh joining PC with missing local KDBX was not classified safe: "+noLocal.Detail);
                            var foreign=Options(null,cloud,Path.Combine(baseDir,"pc-foreign-"+kind));
                            Write(foreign,"v2 UNRELATED DATABASE");
                            var conflict=SyncPreflight.CheckAsync(foreign,CancellationToken.None)
                                .GetAwaiter().GetResult();
                            Assert(conflict.Outcome==PreflightOutcome.Conflict,
                                "Unrelated local KDBX must predict conflict: "+conflict.Detail);
                            Assert(Hashes.Sha256(Working(cloud))==before,
                                "Preflight modified the shared cloud KDBX");
                        });passed++;
                        Check(kind+": preflight detects diverging local and remote edits",()=>{
                            var savedLocal=Path.Combine(baseDir,"restore-local-"+kind+".kdbx");
                            var savedRemote=Path.Combine(baseDir,"restore-remote-"+kind+".kdbx");
                            File.Copy(a.LocalFile,savedLocal,true);
                            File.Copy(Working(cloud),savedRemote,true);
                            var originalStamp=File.GetLastWriteTimeUtc(Working(cloud));
                            try
                            {
                                Write(a,"v2 INDEPENDENT LOCAL CHANGE");
                                var remoteEditor=Options(null,cloud,Path.Combine(baseDir,"remote-editor-"+kind));
                                Write(remoteEditor,"v2 INDEPENDENT CLOUD CHANGE");
                                File.Copy(remoteEditor.LocalFile,Working(cloud),true);
                                File.SetLastWriteTimeUtc(Working(cloud),DateTime.UtcNow.AddSeconds(2));
                                var result=SyncPreflight.CheckAsync(a,CancellationToken.None)
                                    .GetAwaiter().GetResult();
                                Assert(result.Outcome==PreflightOutcome.Conflict,
                                    "Independent local and remote changes must predict conflict: "+result.Detail);
                            }
                            finally
                            {
                                File.Copy(savedLocal,a.LocalFile,true);
                                File.Copy(savedRemote,Working(cloud),true);
                                File.SetLastWriteTimeUtc(Working(cloud),originalStamp);
                            }
                        });passed++;
                        Check(kind+": local switch persists and locks live paths",()=>{
                            Assert(!AppOptions.Load(a.ConfigPath).SyncEnabled,
                                "New profile must be paused by default");
                            SettingsStore.SetSyncEnabled(a.ConfigPath,true);
                            Assert(AppOptions.Load(a.ConfigPath).SyncEnabled,
                                "Enabled state did not persist");
                            var settings=SettingsDraft.Load(a.ConfigPath);
                            settings.LocalFile=Path.Combine(baseDir,"other-"+kind+".kdbx");
                            var blocked=false;
                            try{SettingsStore.Save(settings,a.ConfigPath,a.DataDirectory);}
                            catch(SyncSafetyException){blocked=true;}
                            Assert(blocked,"KDBX path was changeable while synchronization enabled");
                            SettingsStore.SetSyncEnabled(a.ConfigPath,false);
                            Assert(!AppOptions.Load(a.ConfigPath).SyncEnabled,"Disabled state did not persist");
                        });passed++;
                        Check(kind+": scheduled poll skips until due; due poll has metrics",()=>{
                            // Regression: a skipped cloud has no LastMetrics entry.
                            // Network diagnostics must deliberately make a test poll due.
                            var premature=New(a);
                            premature.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
                            Assert(!premature.LastMetrics.ContainsKey(cloud.Name),
                                "Not-yet-due cloud unexpectedly polled");
                            MakeTestCloudDue(a,cloud);
                            var due=New(a);
                            due.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
                            Assert(State(a).Result=="SYNCED","Due idle poll failed");
                            var metrics=RequireCloudMetrics(due,cloud);
                            Assert(metrics.Probe+metrics.List>0,
                                "Due cloud poll performed no metadata checks");
                            Assert(metrics.Get==0 && metrics.Put==0 &&
                                metrics.DownloadBytes+metrics.UploadBytes==0,
                                "Due idle poll unexpectedly transferred content");
                        });passed++;
                        Check(kind+": independently prepared cloud scan commits safely",()=>{
                            var stateBefore=State(a);
                            var preparation=New(a);
                            preparation.PrepareVersionSnapshot(stateBefore);
                            using var staged=preparation.PrepareCloudScanAsync(cloud,stateBefore,
                                Guid.NewGuid().ToString("N"),CancellationToken.None)
                                .GetAwaiter().GetResult();
                            Assert(staged.Error is null && staged.Replica is not null,
                                "Independent cloud scan failed to prepare");
                            var coordinator=New(a);
                            coordinator.RunPreparedAsync(CancellationToken.None,false,
                                new HashSet<string>(StringComparer.Ordinal){cloud.CloudId},
                                new Dictionary<string,V2Engine.ScanOutcome>(StringComparer.Ordinal)
                                    {[cloud.CloudId]=staged}).GetAwaiter().GetResult();
                            Assert(State(a).Result=="SYNCED",
                                "Prepared idle scan must preserve synchronized state");
                            Assert(State(a).V2Cursors[cloud.CloudId].LastSuccessfulScanUtc is not null,
                                "Prepared scan did not commit its cursor");
                        });passed++;
                        Check(kind+": superseded scan cannot roll back cursor",()=>{
                            var stateBefore=State(a);
                            var preparation=New(a);
                            preparation.PrepareVersionSnapshot(stateBefore);
                            using var staged=preparation.PrepareCloudScanAsync(cloud,stateBefore,
                                Guid.NewGuid().ToString("N"),CancellationToken.None)
                                .GetAwaiter().GetResult();
                            MakeTestCloudDue(a,cloud);
                            var later=New(a);
                            later.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
                            var committedAt=State(a).V2Cursors[cloud.CloudId].LastScanUtc;
                            Assert(committedAt is not null && committedAt>staged.StartedUtc,
                                "Test setup did not produce a newer scan");
                            var coordinator=New(a);
                            coordinator.RunPreparedAsync(CancellationToken.None,false,
                                new HashSet<string>(StringComparer.Ordinal){cloud.CloudId},
                                new Dictionary<string,V2Engine.ScanOutcome>(StringComparer.Ordinal)
                                    {[cloud.CloudId]=staged}).GetAwaiter().GetResult();
                            Assert(State(a).V2Cursors[cloud.CloudId].LastScanUtc>=committedAt,
                                "Stale staged scan rolled back a later cloud observation");
                            Assert(State(a).Result=="SYNCED",
                                "Superseded scan must not disturb synchronized state");
                        });passed++;
                        Check(kind+": 100 idle polls, zero content transfer",()=>{
                            long gets=0,puts=0,bytes=0,probes=0;
                            for(int i=0;i<100;i++)
                            {
                                var e=Sync(a);
                                Assert(State(a).Result=="SYNCED","Unexpected idle status");
                                var m=e.LastMetrics["Test"];
                                gets+=m.Get;puts+=m.Put;bytes+=m.DownloadBytes+m.UploadBytes;
                                probes+=m.Probe+m.List;
                            }
                            Emit($"[NET ] {kind}: 100 idle = GET {gets}, PUT {puts}, bytes {bytes}, metadata {probes}");
                            Assert(gets==0 && puts==0 && bytes==0,"Idle poll transferred content or wrote remote data");
                            Assert(probes<=400,"Too many metadata requests during idle");
                        });passed++;
                        Check(kind+": new version and incremental fetch",()=>{
                            var old=State(a).LocalVersionId;
                            Write(a,"v2 CHANGED ON A");Sync(a);
                            Assert(State(a).LocalVersionId!=old,"Local change did not create version");
                            Sync(b);
                            Assert(State(b).Result=="SYNCED","Peer did not pull changed version");
                            Assert(Hashes.Sha256(a.LocalFile)==Hashes.Sha256(b.LocalFile),"Peer content differs");
                        });passed++;
                        Check(kind+": external application saves KDBX",()=>{
                            var old=State(a).LocalVersionId;
                            V2TestFixtures.FileBytes(Working(cloud),"v2 EXTERNAL EDIT");
                            Sync(a);
                            Assert(State(a).Result=="SYNCED","External edit was not adopted");
                            Assert(State(a).LocalVersionId!=old,"External edit produced no revision");
                            Assert(Hashes.Sha256(a.LocalFile)==Hashes.Sha256(Working(cloud)),"External bytes not pulled");
                        });passed++;
                        Check(kind+": local KDBX restored from saved version",()=>{
                            var previous=Hashes.Sha256(a.LocalFile);
                            File.Delete(a.LocalFile);
                            Sync(a);
                            Assert(File.Exists(a.LocalFile),"Deleted local KDBX was not restored");
                            Assert(Hashes.Sha256(a.LocalFile)==previous,"Recovery installed incorrect content");
                        });passed++;
                        Check(kind+": unchanged local bytes do not create a new version",()=>{
                            var previous=State(a).LocalVersionId;
                            var bytes=File.ReadAllBytes(a.LocalFile);
                            File.WriteAllBytes(a.LocalFile,bytes);
                            Sync(a);
                            Assert(State(a).LocalVersionId==previous,"Unchanged bytes created a duplicate version");
                        });passed++;
                        Check(kind+": deletion in cloud cannot delete local KDBX",()=>{
                            File.Delete(Working(cloud));
                            Sync(a);
                            Assert(File.Exists(a.LocalFile),"Local KDBX deleted due to remote deletion");
                            Assert(File.Exists(Working(cloud)),"Remote KDBX not restored");
                        });passed++;
                    }
                    else
                    {
                        Check(kind+": conflict of two offline writers",()=>{
                            Write(a,"v2 OFFLINE EDIT A");Write(b,"v2 OFFLINE EDIT B");
                            Sync(a);Sync(b);
                            Assert(State(b).Conflict,"Independent changes were not flagged");
                            Assert(State(b).ConflictVersionIds.Length>=2,"Both heads must survive");
                            Assert(Directory.GetFiles(Path.Combine(b.DataDirectory,"conflicts"),"*.kdbx").Length>=2,
                                "Missing conflict KDBX copies");
                            var active=ActiveConflictFiles.Read(b.DataDirectory,State(b));
                            Assert(active.Length==State(b).ConflictVersionIds.Length && active.All(v=>v.Ready),
                                "Active conflict versions must resolve to valid local metadata and files");
                            // A historical file must never become an active merge input.
                            V2TestFixtures.FileBytes(Path.Combine(b.DataDirectory,"conflicts",
                                Guid.NewGuid().ToString("N")+".kdbx"),"ARCHIVED CONFLICT FILE");
                            Assert(ActiveConflictFiles.Read(b.DataDirectory,State(b)).Length==active.Length,
                                "Historical copies must not be treated as active heads");
                        });passed++;
                        Check(kind+": manual merge resolves both heads",()=>{
                            var engine=New(b);
                            // A same-length modification must be caught by the
                            // SHA-256 verification, not merely by file length.
                            var source=Path.Combine(b.DataDirectory,"conflicts",
                                State(b).ConflictVersionIds[0]+".kdbx");
                            var original=File.ReadAllBytes(source);
                            var changed=(byte[])original.Clone();
                            changed[^1]^=0x01;
                            bool unsafeCopyRejected=false;
                            try
                            {
                                File.WriteAllBytes(source,changed);
                                try{engine.PrepareMerge();}
                                catch(SyncSafetyException){unsafeCopyRejected=true;}
                            }
                            finally{File.WriteAllBytes(source,original);}
                            Assert(unsafeCopyRejected,"Tampered KDBX must not be prepared for merging");
                            var folder=engine.PrepareMerge();
                            Assert(Directory.GetFiles(folder,"*.kdbx").Length==State(b).ConflictVersionIds.Length,
                                "Workspace must contain only current conflict heads");
                            Assert(File.Exists(Path.Combine(folder,"README_MERGE.txt")),
                                "Workspace must identify required merge inputs");
                            Assert(!MergeCandidatePaths.IsImmutableOriginal(b.DataDirectory,
                                    Directory.GetFiles(folder,"*.kdbx")[0]),
                                "Prepared workspace must be selectable as a merge result");
                            Assert(MergeCandidatePaths.IsImmutableOriginal(b.DataDirectory,
                                    Path.Combine(b.DataDirectory,"conflicts",
                                        State(b).ConflictVersionIds[0]+".kdbx")),
                                "Immutable conflict originals must be rejected by the UI and Core");
                            var names=Directory.GetFiles(folder,"*.kdbx").Select(Path.GetFileName).ToArray();
                            Assert(names.All(n=>n is not null && System.Text.RegularExpressions.Regex.IsMatch(n,
                                @"^Conflict-\d{4}-\d{2}-\d{2}-\d{2}-\d{2}-\d{2}-(Local|External|Merge|Other)-\d{2,}\.kdbx$")),
                                "Merge filenames must use version timestamps and a readable origin");
                            Assert(names.Distinct(StringComparer.Ordinal).Count()==names.Length,
                                "Merge filename collision");
                            Assert(File.ReadAllText(Path.Combine(folder,"README_MERGE.txt"))
                                .Contains("  <-  ",StringComparison.Ordinal),
                                "Merge guide must map each prepared filename to its original VersionId");
                            // Renamed workspace source still MUST NOT be accepted as a merge result.
                            var sourceWorkspaceCopy=Directory.GetFiles(folder,"*.kdbx")[0];
                            var unchangedRejected=false;
                            try{engine.AcceptMergeAsync(sourceWorkspaceCopy,true,CancellationToken.None)
                                .GetAwaiter().GetResult();}
                            catch(SyncSafetyException){unchangedRejected=true;}
                            Assert(unchangedRejected && State(b).Conflict,
                                "An unchanged, renamed source copy must never resolve the conflict");
                            var second=engine.PrepareMerge();
                            Assert(!string.Equals(folder,second,StringComparison.Ordinal) &&
                                Directory.GetFiles(second,"*.kdbx").Length==State(b).ConflictVersionIds.Length,
                                "Every preparation must use a fresh, uncontaminated workspace");
                            var merged=Path.Combine(baseDir,"merged-"+kind+".kdbx");
                            V2TestFixtures.FileBytes(merged,"v2 RESOLVED MERGE");
                            engine.AcceptMergeAsync(merged,true,CancellationToken.None).GetAwaiter().GetResult();
                            Assert(ActiveConflictFiles.Read(b.DataDirectory,State(b)).Length==0,
                                "Old conflict copies must not remain active after merge");
                            Sync(b);
                            Assert(State(b).Result=="SYNCED","Merge not published");
                            Sync(a);
                            Assert(State(a).Result=="SYNCED","Other peer not reconciled after merge");
                        });passed++;
                        Check(kind+": merged working copy may be saved in place",()=>{
                            Write(a,"v2 WORKSPACE MERGE A");
                            Write(b,"v2 WORKSPACE MERGE B");
                            Sync(a);Sync(b);
                            Assert(State(b).Conflict,"A new conflict was not detected");
                            var originalHashes=State(b).ConflictVersionIds.ToDictionary(
                                id=>id,id=>Hashes.Sha256(Path.Combine(b.DataDirectory,"conflicts",id+".kdbx")),
                                StringComparer.Ordinal);
                            var engine=New(b);
                            var workspace=engine.PrepareMerge();
                            var working=Directory.GetFiles(workspace,"*.kdbx")[0];
                            V2TestFixtures.FileBytes(working,"v2 VERIFIED MERGED RESULT SAVED IN PLACE");
                            Assert(engine.InspectMergeCandidate(working) is null,
                                "Edited working copy must not be identified as an unchanged source");
                            // An immutable original may not be selected directly, even with a confirmation.
                            var immutable=Path.Combine(b.DataDirectory,"conflicts",
                                State(b).ConflictVersionIds[0]+".kdbx");
                            bool immutableRejected=false;
                            try{engine.AcceptMergeAsync(immutable,true,CancellationToken.None,
                                    State(b).ConflictVersionIds[0]).GetAwaiter().GetResult();}
                            catch(SyncSafetyException){immutableRejected=true;}
                            Assert(immutableRejected,"Immutable conflict original was accepted as result");
                            engine.AcceptMergeAsync(working,true,CancellationToken.None)
                                .GetAwaiter().GetResult();
                            Assert(!State(b).Conflict,"Modified working copy did not resolve the conflict");
                            Assert(Hashes.Sha256(b.LocalFile)==State(b).LocalSha256,
                                "Installed merge does not match saved state hash");
                            var installed=Path.Combine(b.DataDirectory,"backup",
                                State(b).LocalVersionId+".kdbx");
                            Assert(File.Exists(installed) && Hashes.Sha256(installed)==State(b).LocalSha256,
                                "Merge backup differs from installed database");
                            foreach(var pair in originalHashes)
                                Assert(Hashes.Sha256(Path.Combine(b.DataDirectory,"conflicts",
                                    pair.Key+".kdbx"))==pair.Value,
                                    "Immutable conflict original was changed during acceptance");
                            Sync(b);Sync(a);
                            Assert(State(a).Result=="SYNCED" && State(b).Result=="SYNCED",
                                "Merged working copy was not published to the other peer");
                        });passed++;
                        Check(kind+": accepting an unchanged branch needs explicit identity confirmation",()=>{
                            Write(a,"v2 SELECT BRANCH A");
                            Write(b,"v2 SELECT BRANCH B");
                            Sync(a);Sync(b);
                            Assert(State(b).Conflict,"Expected conflicting branches for manual selection");
                            var engine=New(b);
                            var folder=engine.PrepareMerge();
                            var candidate=Directory.GetFiles(folder,"*.kdbx")[0];
                            var sourceId=engine.InspectMergeCandidate(candidate);
                            Assert(sourceId is not null,"Unmodified prepared copy was not identified");
                            bool rejected=false;
                            try{engine.AcceptMergeAsync(candidate,true,CancellationToken.None)
                                    .GetAwaiter().GetResult();}
                            catch(SyncSafetyException){rejected=true;}
                            Assert(rejected && State(b).Conflict,
                                "Unchanged branch accepted without explicit confirmation");
                            rejected=false;
                            try{engine.AcceptMergeAsync(candidate,true,CancellationToken.None,
                                    Guid.NewGuid().ToString("N")).GetAwaiter().GetResult();}
                            catch(SyncSafetyException){rejected=true;}
                            Assert(rejected && State(b).Conflict,
                                "A confirmation for the wrong original version was accepted");
                            var parents=State(b).ConflictVersionIds.ToArray();
                            engine.AcceptMergeAsync(candidate,true,CancellationToken.None,sourceId)
                                .GetAwaiter().GetResult();
                            Assert(!State(b).Conflict,"Explicitly selected original did not resolve conflict");
                            var version=System.Text.Json.JsonSerializer.Deserialize(
                                File.ReadAllText(Path.Combine(b.DataDirectory,"history",
                                    State(b).LocalVersionId+".json")),SyncJson.Default.VersionInfo);
                            Assert(version is not null,"Merged history record missing");
                            var recordedParents=version!.Parents;
                            Assert(parents.All(id=>recordedParents.Contains(id)),
                                "Explicit branch selection must retain both conflicting parents");
                            Sync(b);Sync(a);
                            Assert(State(a).Result=="SYNCED" && State(b).Result=="SYNCED",
                                "Explicit branch selection was not published");
                        });passed++;
                        Check(kind+": immutable original tampering blocks merge acceptance",()=>{
                            Write(a,"v2 ORIGINAL CHECK A");
                            Write(b,"v2 ORIGINAL CHECK B");
                            Sync(a);Sync(b);
                            Assert(State(b).Conflict,"Expected conflict for original integrity test");
                            var engine=New(b);
                            var folder=engine.PrepareMerge();
                            var candidate=Directory.GetFiles(folder,"*.kdbx")[0];
                            V2TestFixtures.FileBytes(candidate,"v2 MODIFIED WORKSPACE TEST");
                            var original=Path.Combine(b.DataDirectory,"conflicts",
                                State(b).ConflictVersionIds[0]+".kdbx");
                            var bytes=File.ReadAllBytes(original);
                            var altered=(byte[])bytes.Clone();
                            altered[^1]^=0x01;
                            bool rejected=false;
                            try
                            {
                                File.WriteAllBytes(original,altered);
                                try{engine.AcceptMergeAsync(candidate,true,CancellationToken.None)
                                        .GetAwaiter().GetResult();}
                                catch(SyncSafetyException){rejected=true;}
                            }
                            finally{File.WriteAllBytes(original,bytes);}
                            Assert(rejected && State(b).Conflict,
                                "Modified immutable source was not rejected at acceptance");
                            engine.AcceptMergeAsync(candidate,true,CancellationToken.None)
                                .GetAwaiter().GetResult();
                            Sync(b);Sync(a);
                            Assert(State(a).Result=="SYNCED" && State(b).Result=="SYNCED",
                                "Acceptance failed after restoring original source");
                        });passed++;
                        Check(kind+": interrupted version publication is restartable",()=>{
                            Write(a,"v2 INTERRUPT AFTER IMMUTABLE BODY");
                            var aborted=false;
                            var engine=New(a);
                            engine.PublicationHookForTests=(_,step,_)=>{
                                if(step!="body-created" || aborted)return Task.CompletedTask;
                                aborted=true;throw new IOException("Injected network failure");
                            };
                            engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
                            Assert(aborted,"Fault hook not triggered");
                            var saved=State(a).LocalVersionId;
                            Sync(a);
                            Assert(State(a).Result=="SYNCED","Failed publication did not resume");
                            Assert(State(a).LocalVersionId==saved,"Restart duplicated the version");
                        });passed++;
                        if(hasCas)
                        {
                            Check("CAS: concurrent external overwrite is rejected",()=>{
                                Write(a,"v2 CONCURRENT LOCAL SAVE");
                                var engine=New(a);
                                engine.PublicationHookForTests=(_,step,_)=>{
                                    if(step=="before-working-replace")
                                        V2TestFixtures.FileBytes(Working(cloud),"v2 SIMULTANEOUS EXTERNAL SAVE");
                                    return Task.CompletedTask;
                                };
                                engine.RunOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
                                Assert(State(a).Result=="ERROR","CAS collision should reject replacement");
                                Assert(File.ReadAllBytes(Working(cloud)).AsSpan().IndexOf(
                                    System.Text.Encoding.UTF8.GetBytes("SIMULTANEOUS EXTERNAL"))>=0,
                                    "External writer was overwritten despite CAS");
                                Sync(a);
                                Assert(State(a).Conflict,"Competing edits must become a conflict");
                            });passed++;
                        }
                    }
                }
                catch(Exception e)
                {
                    failed++;
                    if(e is not ReportedCheckException)Emit("[FAIL] "+kind+": "+e.GetType().Name+": "+e.Message);
                    Emit("       Reason: "+e);
                }
            }
        if(!safety)
        {
            var multiCloudFolder=Path.Combine(baseDir,"multi-cloud");
            var healthyCloud=FakeCloud(Path.Combine(multiCloudFolder,"good"),true) with {Name="Good",CloudId="Good"};
            var badRoot=Path.Combine(multiCloudFolder,"blocked");
            Directory.CreateDirectory(multiCloudFolder);
            File.WriteAllText(badRoot,"blocked backend");
            var bad=FakeCloud(badRoot,true) with {Name="TemporarilyUnavailable",CloudId="TemporarilyUnavailable"};
            var multiCloudOptions=Options(null,healthyCloud,Path.Combine(multiCloudFolder,"computer"));
            multiCloudOptions.Clouds.Add(bad);
            try
            {
                Check("multicloud: partial save and later recovery",()=>{
                    Write(multiCloudOptions,"v2 PARTIAL CLOUD FAILURE");
                    Sync(multiCloudOptions);
                    Assert(State(multiCloudOptions).Result=="PARTIAL","Unavailable cloud should produce PARTIAL");
                    Assert(File.Exists(Working(healthyCloud)),"Working copy not saved in healthy cloud");
                    File.Delete(badRoot);
                    Sync(multiCloudOptions);
                    Assert(State(multiCloudOptions).Result=="SYNCED","Recovered cloud did not converge");
                    Assert(File.Exists(Working(bad)),"Missing restored cloud copy");
                });passed++;
            }
            catch(Exception e){failed++;if(e is not ReportedCheckException)Emit("[FAIL] Multicloud: "+e.Message);}
            try
            {
                var folder=Path.Combine(baseDir,"partial-preflight");
                var good=FakeCloud(Path.Combine(folder,"good"),true)
                    with {Name="Reachable",CloudId="partial-reachable"};
                // No credentials: an intentionally inaccessible cloud, without
                // any interaction with a real provider or the user's vault.
                var unavailable=new CloudOptions("NoCredentials","GoogleDrive","vault","",
                    "","","","","","","",30,CloudId:"partial-no-auth");
                var c=Options(null,good,Path.Combine(folder,"computer"),unavailable);
                Check("partial preflight: one verified and one unauthenticated cloud",()=>
                {
                    Write(c,"PARTIAL PREFLIGHT TEST");
                    var before=Hashes.Sha256(c.LocalFile);
                    var report=SyncPreflight.CheckAsync(c,CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert(report.Outcome==PreflightOutcome.NoConflict &&
                        report.VerifiedClouds==1 && report.UnavailableClouds==1,
                        "A verified cloud must allow partial activation: "+report.Detail);
                    Assert(report.Detail.Contains("NoCredentials",StringComparison.Ordinal),
                        "Preflight must disclose the unverified replica");
                    var second=SyncPreflight.CheckAsync(c,CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert(second.Outcome==report.Outcome && second.Snapshot==report.Snapshot,
                        "Repeated partial preflight must have a stable snapshot");
                    Assert(Hashes.Sha256(c.LocalFile)==before,
                        "Read-only preflight changed the local database");
                });passed++;
                Check("partial engine: unavailable replica does not block verified cloud",()=>
                {
                    Sync(c);
                    Assert(State(c).Result=="PARTIAL",
                        "Unavailable cloud must not prevent publication to available replica");
                    Assert(File.Exists(Working(good)),
                        "Available replica did not receive the database");
                    Assert(State(c).Clouds[unavailable.Name].State=="NEEDS_AUTH",
                        "Unavailable replica must retain its own authentication status");
                    TopologyCoordinator.RunAsync(c,new SyncLog(c),CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert(TopologyCoordinator.Load(c) is not null,
                        "Shared topology must work with an unavailable replica");
                    var pre=SyncPreflight.CheckAsync(c,CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert(pre.Outcome==PreflightOutcome.NoConflict && pre.VerifiedClouds==1,
                        "Partial publication should remain safe to re-check: "+pre.Detail);
                });passed++;
                Check("partial preflight: missing reachable topology still blocks",()=>
                {
                    var current=Path.Combine(new Uri(good.BaseUrl).LocalPath,
                        good.RemoteDirectory,"topology.json");
                    var held=current+".held";
                    Assert(File.Exists(current),"Required topology fixture missing");
                    File.Move(current,held);
                    try
                    {
                        var pre=SyncPreflight.CheckAsync(c,CancellationToken.None)
                            .GetAwaiter().GetResult();
                        Assert(pre.Outcome==PreflightOutcome.Undetermined,
                            "A reachable cloud with missing topology must not be bypassed");
                    }
                    finally{File.Move(held,current);}
                });passed++;
                Check("partial recovery: foreign vault must never be overwritten",()=>
                {
                    var other=FakeCloud(Path.Combine(folder,"foreign-remote"),true)
                        with {Name="ForeignVault",CloudId="partial-foreign"};
                    var foreign=Options(null,other,Path.Combine(folder,"foreign-computer"));
                    Write(foreign,"INDEPENDENT VAULT ID");
                    Sync(foreign);
                    var original=Hashes.Sha256(Working(other));
                    c.Clouds.Add(other);
                    try
                    {
                        var result=SyncPreflight.CheckAsync(c,CancellationToken.None)
                            .GetAwaiter().GetResult();
                        Assert(result.Outcome==PreflightOutcome.Undetermined,
                            "An accessible foreign VaultId must block partial activation");
                        Sync(c);
                        Assert(State(c).Result=="ERROR",
                            "A foreign vault must cause the running engine to stop publication");
                        Assert(Hashes.Sha256(Working(other))==original,
                            "Foreign remote KDBX was modified after reconnection");
                    }
                    finally{c.Clouds.Remove(other);}
                });passed++;
                Check("preflight: zero verified clouds blocks activation",()=>
                {
                    var only=Options(null,unavailable,Path.Combine(folder,"no-cloud"));
                    Write(only,"NO CLOUD SHOULD BE WRITTEN");
                    var result=SyncPreflight.CheckAsync(only,CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert(result.Outcome==PreflightOutcome.Undetermined && result.Snapshot.Length==0,
                        "No verified replica must not enable synchronization");
                });passed++;
                Check("preflight: transport failures may be skipped but integrity failures cannot",()=>
                {
                    Assert(PreflightAvailability.IsTemporary(good,
                        new HttpRequestException("connection refused")),
                        "Network outage classification missing");
                    Assert(PreflightAvailability.IsTemporary(good,
                        new HttpRequestException("401",null,System.Net.HttpStatusCode.Unauthorized)),
                        "Missing authorization should count as unavailable");
                    Assert(!PreflightAvailability.IsTemporary(good,
                        new HttpRequestException("409",null,System.Net.HttpStatusCode.Conflict)),
                        "Conflicting remote response must not be excluded");
                    Assert(!PreflightAvailability.IsTemporary(good,
                        new SyncSafetyException("Different vault identities")),
                        "Incompatible vault must never be skipped");
                    Assert(!PreflightAvailability.IsTemporary(good,
                        new SyncSafetyException("Corrupted metadata")),
                        "Invalid immutable metadata must never be skipped");
                    Assert(!PreflightAvailability.IsTemporary(good,
                        new InvalidOperationException("Unexpected serialization failure")),
                        "Unexpected program errors must not be hidden by partial preflight");
                });passed++;
            }
            catch(Exception e)
            {
                failed++;
                if(e is not ReportedCheckException)Emit("[FAIL] Partial preflight: "+e.Message);
            }
            try
            {
                var topoCloud=FakeCloud(Path.Combine(baseDir,"topology-storage"),true);
                var a=Options(null,topoCloud,Path.Combine(baseDir,"topology-pc-a"));
                var b=Options(null,topoCloud,Path.Combine(baseDir,"topology-pc-b"));
                Check("topology: initial cloud and PC registration",()=>
                {
                    Write(a,"TOPOLOGY INITIAL");Sync(a);
                    var result=TopologyCoordinator.RunAsync(a,new SyncLog(a),CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert(TopologyCoordinator.Load(a)?.Computers.Count==1,"Missing initial computer");
                    var shared=TopologyCoordinator.Load(a)!;
                    Assert(shared.DefaultCloudPollSeconds==a.PollSeconds &&
                           shared.DebounceMs==a.DebounceMs && shared.RetrySeconds==a.RetrySeconds,
                           "Shared debounce/retry/default cloud interval not published");
                    Assert(result.Left==false,"Unexpected topology leave");
                    Assert(!File.Exists(TopologyCoordinator.PendingPath(a)),
                        "Initial topology registration created a spurious pending edit");
                    var remote=Path.Combine(new Uri(topoCloud.BaseUrl).LocalPath,"vault","topology.json");
                    Assert(File.Exists(remote),"Topology not uploaded");
                    var json=File.ReadAllText(remote);
                    Assert(!json.Contains("SecretKey",StringComparison.OrdinalIgnoreCase) &&
                           !json.Contains("RefreshCredential",StringComparison.OrdinalIgnoreCase),
                           "Topology carries credential data");
                });passed++;
                Check("topology: paused mode exchanges metadata without any KDBX access",()=>
                {
                    var remoteRoot=Path.Combine(new Uri(topoCloud.BaseUrl).LocalPath,"vault");
                    var working=Path.Combine(remoteRoot,"database.kdbx");
                    var pointer=Path.Combine(remoteRoot,"devices",State(a).InstallationId+".json");
                    var beforeWorking=Hashes.Sha256(working);
                    var beforePointer=Hashes.Sha256(pointer);
                    var beforeBackups=Directory.GetFiles(Path.Combine(remoteRoot,"versions"),"*.kdbx")
                        .OrderBy(x=>x,StringComparer.Ordinal).Select(x=>Path.GetFileName(x)+"|"+Hashes.Sha256(x)).ToArray();
                    // A previous topology application must never enqueue a new
                    // local edit. Keep the original error if this is violated.
                    Assert(!File.Exists(TopologyCoordinator.PendingPath(a)),
                        "Unexpected queued topology update before paused rename");
                    // This is a control-plane edit, not a change in the KDBX or its pointer.
                    TopologyCoordinator.QueueRenameComputer(a,State(a).InstallationId,"Paused Host");
                    TopologyCoordinator.PollPausedAsync(a,new SyncLog(a),CancellationToken.None,true)
                        .GetAwaiter().GetResult();
                    Assert(TopologyCoordinator.Load(a)?.Computers.Single(x=>x.Id==State(a).InstallationId).Name=="Paused Host",
                        "Paused computer did not publish renamed topology");
                    Assert(!File.Exists(TopologyCoordinator.PendingPath(a)),
                        "Successful paused topology publication left a pending edit");
                    // Simulate a crash after a pending revision reached the cloud
                    // but before the local pending file was acknowledged.
                    // Replaying an already-applied change must be safe, must not
                    // re-upload or change the database, and must unblock edits.
                    var applied=TopologyCoordinator.Load(a)!;
                    SyncStorage.SaveJsonAtomic(TopologyCoordinator.PendingPath(a),
                        new TopologyPending{BasedOnChangeId=Guid.NewGuid().ToString("N"),
                            Desired=applied},TopologyJson.Default.TopologyPending);
                    TopologyCoordinator.RunAsync(a,new SyncLog(a),CancellationToken.None,
                        force:true,announceKdbxPointer:false).GetAwaiter().GetResult();
                    Assert(!File.Exists(TopologyCoordinator.PendingPath(a)),
                        "Already-applied pending topology edit was not acknowledged on restart");
                    Assert(State(a).Clouds[topoCloud.Name].State=="TOPOLOGY_OK",
                        "Paused cloud topology probe did not mark connection alive");
                    Assert(Hashes.Sha256(pointer)==beforePointer,
                        "Pausing KDBX changed the device version pointer");
                    // Brand-new device joins control plane without a local vault.
                    Assert(!File.Exists(b.LocalFile),"Test setup unexpectedly created new PC vault");
                    TopologyCoordinator.PollPausedAsync(b,new SyncLog(b),CancellationToken.None,true)
                        .GetAwaiter().GetResult();
                    Assert(TopologyCoordinator.Load(b)?.Computers.Count(x=>x.Active)==2,
                        "Paused joining computer was not registered in topology");
                    Assert(!File.Exists(TopologyCoordinator.PendingPath(b)),
                        "Paused joining computer queued an unintended shared edit");
                    Assert(!File.Exists(b.LocalFile),"Paused join unexpectedly downloaded KDBX");
                    Assert(Hashes.Sha256(working)==beforeWorking,
                        "Paused control-plane exchange modified working KDBX");
                    Assert(Hashes.Sha256(pointer)==beforePointer,
                        "Paused control-plane exchange modified existing KDBX pointer");
                    var afterBackups=Directory.GetFiles(Path.Combine(remoteRoot,"versions"),"*.kdbx")
                        .OrderBy(x=>x,StringComparer.Ordinal).Select(x=>Path.GetFileName(x)+"|"+Hashes.Sha256(x)).ToArray();
                    Assert(beforeBackups.SequenceEqual(afterBackups),
                        "Paused control-plane exchange modified immutable backups");
                    // B's registration advances topology while A still has the
                    // previous local copy: never display a false green preflight.
                    var stale=SyncPreflight.CheckAsync(a,CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert(stale.Outcome==PreflightOutcome.Undetermined,
                        "Outdated local topology incorrectly allowed KDBX startup: "+stale.Detail);
                    TopologyCoordinator.PollPausedAsync(a,new SyncLog(a),CancellationToken.None,true)
                        .GetAwaiter().GetResult();
                    var aligned=SyncPreflight.CheckAsync(a,CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert(aligned.Outcome==PreflightOutcome.NoConflict,
                        "Converged topology and unchanged KDBX incorrectly blocked startup: "+aligned.Detail);
                    Assert(!File.Exists(TopologyCoordinator.PendingPath(a)),
                        "Received peer topology re-queued a local edit");
                });passed++;
                Check("topology: second computer joins without shared secrets",()=>
                {
                    Sync(b);
                    TopologyCoordinator.RunAsync(b,new SyncLog(b),CancellationToken.None).GetAwaiter().GetResult();
                    Assert(TopologyCoordinator.Load(b)?.Computers.Count(x=>x.Active)==2,
                        "Joining computer missing from topology");
                    var shared=TopologyCoordinator.Load(b)!;
                    Assert(shared.DefaultCloudPollSeconds==a.PollSeconds &&
                           shared.DebounceMs==a.DebounceMs && shared.RetrySeconds==a.RetrySeconds,
                           "Shared synchronization settings did not reach joining computer");
                    // A second data-plane pass must now install KDBX immediately.
                    // A successful topology registration must not cause a loop of
                    // self-induced TOPOLOGY_CHANGED results.
                    Sync(b);
                    Assert(!State(b).TopologyRefreshNeeded,
                        "Joined computer treated its own topology revision as a new change");
                    Assert(State(b).Result=="SYNCED",
                        "Joined computer did not complete initial data synchronization: "+State(b).Result);
                    Assert(File.Exists(b.LocalFile),"Joined computer did not obtain KDBX");
                    Assert(Hashes.Sha256(a.LocalFile)==Hashes.Sha256(b.LocalFile),
                        "Joined computer downloaded different KDBX content");
                });passed++;
                Check("topology: shared retry, debounce and cloud default reach second PC",()=>
                {
                    var draft=SettingsDraft.Load(a.ConfigPath);
                    draft.Retry=91;
                    draft.Debounce=4321;
                    draft.Poll=300;
                    SettingsStore.Save(draft,a.ConfigPath,a.DataDirectory);
                    TopologyCoordinator.RunAsync(AppOptions.Load(a.ConfigPath),
                        new SyncLog(a),CancellationToken.None).GetAwaiter().GetResult();
                    // Remote revision announcement becomes visible to PC-B during
                    // its usual sync; then it installs the shared values.
                    Sync(b);
                    TopologyCoordinator.RunAsync(b,new SyncLog(b),CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var received=AppOptions.Load(b.ConfigPath);
                    Assert(received.RetrySeconds==91 && received.DebounceMs==4321 &&
                           received.PollSeconds==300,
                           "Second computer did not apply published shared settings");
                });passed++;
                Check("topology: rename computer without changing its ID",()=>
                {
                    var id=State(b).InstallationId;
                    Sync(a);
                    TopologyCoordinator.RunAsync(a,new SyncLog(a),CancellationToken.None).GetAwaiter().GetResult();
                    TopologyCoordinator.QueueRenameComputer(a,id,"Office Laptop");
                    TopologyCoordinator.RunAsync(a,new SyncLog(a),CancellationToken.None).GetAwaiter().GetResult();
                    Sync(b);
                    TopologyCoordinator.RunAsync(b,new SyncLog(b),CancellationToken.None).GetAwaiter().GetResult();
                    Assert(TopologyCoordinator.Load(b)?.Computers.Single(x=>x.Id==id).Name=="Office Laptop",
                        "Renamed device label did not reach second computer");
                    Assert(State(b).InstallationId==id,"Rename changed device identity");
                });passed++;
                Check("topology: exclude computer and stop on next check",()=>
                {
                    Sync(a);
                    TopologyCoordinator.RunAsync(a,new SyncLog(a),CancellationToken.None).GetAwaiter().GetResult();
                    TopologyCoordinator.QueueRemoveComputer(a,State(b).InstallationId,false);
                    TopologyCoordinator.RunAsync(a,new SyncLog(a),CancellationToken.None).GetAwaiter().GetResult();
                    Sync(b);
                    TopologyCoordinator.RunAsync(b,new SyncLog(b),CancellationToken.None).GetAwaiter().GetResult();
                    Assert(State(b).TopologyDisabled,"Excluded computer continued operating");
                    Assert(File.Exists(b.LocalFile),"Excluded computer lost its local KDBX");
                });passed++;
                Check("topology: rejoin requires new installation identity",()=>
                {
                    var state=State(b);var old=state.InstallationId;
                    state.InstallationId=Guid.NewGuid().ToString("N");
                    state.TopologyDisabled=false;
                    SyncStorage.SaveState(b.StateFile,state);
                    File.Delete(TopologyCoordinator.CachePath(b));
                    TopologyCoordinator.RunAsync(b,new SyncLog(b),CancellationToken.None).GetAwaiter().GetResult();
                    Assert(TopologyCoordinator.Load(b)?.Computers.Count(x=>x.Active)==2,
                        "Rejoined computer not registered");
                    Assert(State(b).InstallationId!=old,"Rejoin used revoked identity");
                });passed++;
                Check("usage: daily, monthly and cumulative counts",()=>
                {
                    var state=State(a);
                    state.V2Totals["Test"]=new V2OperationTotals{Get=15,Put=3,Probe=40,
                        UploadBytes=1000000,DownloadBytes=2400};
                    UsageStatistics.Record(a,state);
                    var usage=UsageStatistics.Load(a);
                    var cloud=usage.Self.Clouds["Test"];
                    Assert(UsageStatistics.Period(cloud,"all").Get==15,"Cumulative GET count wrong");
                    Assert(UsageStatistics.Period(cloud,"month").Put==3,"Monthly PUT count wrong");
                    Assert(UsageStatistics.Period(cloud,"week").DownloadBytes==2400,"Weekly traffic wrong");
                });passed++;
                Check("usage: local UI includes unreported cycle and avoids double counting",()=>
                {
                    var state=State(a);
                    // The previously recorded values represent the latest published snapshot.
                    // Another completed local cycle must immediately appear in this PC's UI,
                    // regardless of the hourly remote stats publication interval.
                    var published=UsageStatistics.Load(a);
                    var before=published.Self.Clouds["Test"].Total.Copy();
                    published.Peers["other-computer"]=new UsageSnapshot{
                        InstallationId="other-computer",ComputerName="Peer",
                        Clouds=new Dictionary<string,UsageCloud>(StringComparer.Ordinal){
                            ["Test"]=new UsageCloud{CloudId="Test",Name="Test",
                                Total=new V2OperationTotals{Get=8,Put=2}}}};
                    UsageStatistics.Save(a,published);
                    // Continue from the exact recorded baseline, not from the
                    // test engine's unrelated persisted transport totals.
                    state.V2Totals["Test"]=published.LastEngineTotals["Test"].Copy();
                    state.V2Totals["Test"].Get+=7;
                    state.V2Totals["Test"].Put+=2;
                    state.V2Totals["Test"].DownloadBytes+=3300;
                    var live=UsageStatistics.LoadForDisplay(a,state);
                    var own=live.Self.Clouds["Test"].Total;
                    Assert(own.Get==before.Get+7 && own.Put==before.Put+2,
                        "UI did not include operations from the current unpublished cycle");
                    Assert(own.DownloadBytes==before.DownloadBytes+3300,
                        "UI did not include currently measured traffic");
                    Assert(live.Peers["other-computer"].Clouds["Test"].Total.Get==8,
                        "Live local overlay changed another computer's published statistics");
                    Assert(UsageStatistics.Load(a).Self.Clouds["Test"].Total.Get==before.Get,
                        "Displaying local totals accidentally published/saved a snapshot");
                    Assert(UsageStatistics.LoadForDisplay(a,state).Self.Clouds["Test"].Total.Get==before.Get+7,
                        "Repeated UI refresh counted the same operations twice");
                    UsageStatistics.Record(a,state);
                    Assert(UsageStatistics.LoadForDisplay(a,state).Self.Clouds["Test"].Total.Get==before.Get+7,
                        "Persisting the local totals counted already displayed operations twice");
                });passed++;
            }
            catch(Exception e){failed++;if(e is not ReportedCheckException)Emit("[FAIL] Topology/usage: "+e.Message);}
        }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KPS_ENABLE_TEST_STORE",prev);
            if(failed==0)
            {
                try{Directory.Delete(baseDir,true);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            else Emit("[INFO] Тестовые данные сохранены: "+baseDir);
        }
        Emit($"Summary: {passed} PASS, {failed} FAIL ("+(passed+failed)+" total)");
        SaveReport(options,safety?"safety":"local");
        return failed==0?0:1;
    }
    internal static int Run(AppOptions? options=null)=>RunSuite(false,options);
    internal static int RunSafety(AppOptions? options=null)=>RunSuite(true,options);

    // Live-cloud diagnostics: strictly use a random dedicated test directory.
    internal static int RunCloudIntegration(AppOptions source,string selected,CancellationToken cancel=default)
        =>RunCloud(source,selected,false,cancel);
    internal static int RunCloudRace(AppOptions source,string selected,CancellationToken cancel=default)
        =>RunCloud(source,selected,true,cancel);
    private static int RunCloud(AppOptions production,string selected,bool race,CancellationToken cancel)
    {
        StartReport();
        var matching=production.Clouds.Where(c=>selected.Length==0 ||
            c.Name.Equals(selected,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matching.Length==0){Emit("[FAIL] Unknown cloud");return 1;}
        Emit("KeePassSync tests: v2 isolated cloud "+(race?"two-device race":"connectivity and efficiency"));
        var passed=0;var failed=0;
        var casePassed=0;var caseFailed=0;
        foreach(var cloud in matching)
        {
            var remote=IntegrationIsolation.NewRunRoot(cloud.RemoteDirectory);
            var testCloud=cloud with {RemoteDirectory=remote};
            var local=Path.Combine(Path.GetTempPath(),"kps-v2-live-"+Guid.NewGuid().ToString("N"));
            var a=Options(production,testCloud,Path.Combine(local,"A"));
            var b=Options(production,testCloud,Path.Combine(local,"B"));
            bool success=false;
            var stage="initial cloud bootstrap";
            Emit("       Test-only folder: "+remote);
            try
            {
                cancel.ThrowIfCancellationRequested();
                Write(a,"v2 LIVE INITIAL");
                var e=New(a);e.RunOnceAsync(cancel).GetAwaiter().GetResult();
                Assert(State(a).Result=="SYNCED","Cloud bootstrap failed: "+State(a).Error);
                var initial=RequireCloudMetrics(e,testCloud);
                casePassed++;Emit("[PASS] "+cloud.Name+" / initial cloud bootstrap");
                Emit($"[NET ] Initial logical calls: GET {initial.Get}, PUT {initial.Put}, LIST {initial.List}, PROBE {initial.Probe}");
                stage="idle polling without content upload";
                // The normal interval has not yet elapsed: make this isolated
                // client's next scheduled poll due, so the idle check is real.
                MakeTestCloudDue(a,testCloud);
                e=New(a);e.RunOnceAsync(cancel).GetAwaiter().GetResult();
                Assert(State(a).Result=="SYNCED","Idle poll failed: "+State(a).Error);
                var idle=RequireCloudMetrics(e,testCloud);
                Emit($"[NET ] Idle logical calls: GET {idle.Get}, PUT {idle.Put}, LIST {idle.List}, PROBE {idle.Probe}, bytes {idle.DownloadBytes+idle.UploadBytes}");
                Assert(idle.Get==0 && idle.Put==0 && idle.DownloadBytes+idle.UploadBytes==0,
                    "Unchanged cloud transferred content");
                casePassed++;Emit("[PASS] "+cloud.Name+" / idle polling without content upload");
                stage="second device bootstrap";
                e=New(b);e.RunOnceAsync(cancel).GetAwaiter().GetResult();
                Assert(State(b).Result=="SYNCED","Peer bootstrap failed");
                casePassed++;Emit("[PASS] "+cloud.Name+" / second device bootstrap");
                if(race)
                {
                    stage="independent concurrent edits and conflict retention";
                    Write(a,"v2 CLOUD RACE A");Write(b,"v2 CLOUD RACE B");
                    New(a).RunOnceAsync(cancel).GetAwaiter().GetResult();
                    New(b).RunOnceAsync(cancel).GetAwaiter().GetResult();
                    Assert(State(b).Conflict,"Cloud race did not retain independent versions");
                    Assert(State(b).ConflictVersionIds.Length>=2,"Conflict heads not preserved");
                    casePassed++;Emit("[PASS] "+cloud.Name+" / independent concurrent edits and conflict retention");
                }
                else
                {
                    stage="delta upload";
                    Write(a,"v2 LIVE DELTA");
                    New(a).RunOnceAsync(cancel).GetAwaiter().GetResult();
                    Assert(State(a).Result=="SYNCED","Delta upload failed");
                    casePassed++;Emit("[PASS] "+cloud.Name+" / delta upload");
                    stage="delta download and KDBX equality";
                    // PC-B has not reached its real poll interval yet. Trigger
                    // a due poll explicitly rather than silently skipping it.
                    MakeTestCloudDue(b,testCloud);
                    var peerDelta=New(b);
                    peerDelta.RunOnceAsync(cancel).GetAwaiter().GetResult();
                    RequireCloudMetrics(peerDelta,testCloud);
                    Assert(State(b).Result=="SYNCED","Delta download failed: "+State(b).Error);
                    Assert(Hashes.Sha256(a.LocalFile)==Hashes.Sha256(b.LocalFile),"Different remote KDBX after sync");
                    casePassed++;Emit("[PASS] "+cloud.Name+" / delta download and KDBX equality");
                }
                success=true;
            }
            catch(Exception ex){failed++;caseFailed++;Emit("[FAIL] "+cloud.Name+" / "+stage+": "+ex.Message);}
            finally
            {
                Emit("[CLEANUP] Удаляю только отдельную тестовую папку "+remote);
                try
                {
                    if(!IntegrationIsolation.IsTestRunRoot(remote))
                        throw new SyncSafetyException("Unsafe cleanup path");
                    using var cleanup=CreateProvider(production,testCloud);
                    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    cleanup.DeleteTestDirectoryAsync(remote,timeout.Token).GetAwaiter().GetResult();
                    Emit("[CLEANUP] Тестовый каталог удалён");
                    casePassed++;Emit("[PASS] "+cloud.Name+" / isolated test directory cleanup");
                }
                catch(Exception ex){failed++;caseFailed++;success=false;Emit("[FAIL] "+cloud.Name+" / isolated test directory cleanup: "+ex.Message);}
                if(success){passed++;try{Directory.Delete(local,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
                else Emit("[INFO] Локальные тестовые данные сохранены: "+local);
            }
        }
        Emit($"Summary: {casePassed} PASS, {caseFailed} FAIL ("+(casePassed+caseFailed)+" total)");
        SaveReport(production,race?"cloud-race":"cloud");
        return failed==0 && passed==matching.Length?0:1;
    }
    private static IRemoteStore CreateProvider(AppOptions opt,CloudOptions cloud)
    {
        var cfg=opt.AsTransportConfig(cloud);
        return cloud.Provider switch{
            "GoogleDrive"=>new GoogleDriveStore(cfg),"WebDAV"=>new WebDavStore(cfg),
            "S3" or "CloudflareR2"=>new S3Store(cfg),"ProtonDrive"=>new ProtonDriveStore(cfg),
            _=>throw new NotSupportedException("Unsupported live cloud diagnostics provider")};
    }
}
