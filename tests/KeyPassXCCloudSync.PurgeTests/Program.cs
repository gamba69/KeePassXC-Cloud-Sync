using KeyPassXCCloudSync;

internal static class Program
{
    private static int _passed;
    private static void Assert(bool ok,string reason)
    {if(!ok)throw new Exception(reason);}
    private static void Test(string name,Action test)
    {TestReporter.Run("PurgeTests",name,test);_passed++;}

    private sealed class Fixture : IDisposable
    {
        private readonly string _root=Path.Combine(Path.GetTempPath(),"kpcs-purgetest-"+Guid.NewGuid().ToString("N"));
        internal readonly string Self=Guid.NewGuid().ToString("N");
        internal readonly string OldComputer=Guid.NewGuid().ToString("N");
        internal readonly string ActiveCloud=Guid.NewGuid().ToString("N");
        internal readonly string OldCloud=Guid.NewGuid().ToString("N");
        internal readonly AppOptions Cfg;
        internal TopologyDocument Doc;
        internal Fixture()
        {
            var data=Path.Combine(_root,"data");
            Directory.CreateDirectory(data);
            Cfg=new AppOptions
            {
                ConfigPath=Path.Combine(_root,"test-only.ini"),LocalFile=Path.Combine(_root,"dummy.kdbx"),
                StateFile=Path.Combine(data,"state.json"),DataDirectory=data,
                Clouds=[new CloudOptions("Active","FileSystemTest","vault",new Uri(_root).AbsoluteUri,
                    "","","","","","","",30,CloudId:ActiveCloud)],
                TrayEnabled=false,SyncEnabled=false,PollSeconds=60,StatsPublishMinutes=60,
                DebounceMs=0,StableMs=100,RetrySeconds=60,NotifyAfterMinutes=10,
                BackupCopies=20,BackupDays=30,BackupOnStartup=false,StartupBackupKeep=30,
                LogLevel="Info",LogSizeMB=5,LogFiles=5
            };
            SyncStorage.SaveState(Cfg.StateFile,new AppState{InstallationId=Self});
            Doc=new TopologyDocument
            {
                TopologyId=Guid.NewGuid().ToString("N"),ChangeId=Guid.NewGuid().ToString("N"),
                Revision=1,WriterId=Self,Clouds=[TopologyCoordinator.Public(Cfg.Clouds[0])],
                RemovedClouds=[new TopologyRemovedCloud{Id=OldCloud,Name="Deleted cloud",Provider="WebDAV"}],
                Computers=[new TopologyComputer{Id=Self,Name="Current"},
                           new TopologyComputer{Id=OldComputer,Name="Deleted computer",Active=false}]
            };
            WriteTopology(Doc);
            var usage=new UsageLocal
            {
                Self=new UsageSnapshot{InstallationId=Self,Clouds=new()
                {
                    [ActiveCloud]=new UsageCloud{CloudId=ActiveCloud,Name="Active cloud"},
                    [OldCloud]=new UsageCloud{CloudId=OldCloud,Name="Deleted cloud"}
                }},
                Peers=new()
                {
                    [OldComputer]=new UsageSnapshot{InstallationId=OldComputer,
                        Clouds=new(){[ActiveCloud]=new UsageCloud{CloudId=ActiveCloud,Name="Active cloud"}}},
                    [Guid.NewGuid().ToString("N")]=new UsageSnapshot{InstallationId=Guid.NewGuid().ToString("N"),
                        Clouds=new(){[OldCloud]=new UsageCloud{CloudId=OldCloud,Name="Deleted cloud"}}}
                },
                LastPublishedUtc=DateTimeOffset.UtcNow
            };
            UsageStatistics.Save(Cfg,usage);
        }
        internal void WriteTopology(TopologyDocument doc)
            =>SyncStorage.SaveJsonAtomic(TopologyCoordinator.CachePath(Cfg),doc,TopologyJson.Default.TopologyDocument);
        internal TopologyPending Pending() => System.Text.Json.JsonSerializer.Deserialize(
            File.ReadAllText(TopologyCoordinator.PendingPath(Cfg)),TopologyJson.Default.TopologyPending)!;
        internal void CommitPending()
        {
            var next=Pending().Desired;
            WriteTopology(next);
            UsageStatistics.PruneSaved(Cfg,next);
            File.Delete(TopologyCoordinator.PendingPath(Cfg));
            Doc=next;
        }
        public void Dispose()
        {if(Directory.Exists(_root))Directory.Delete(_root,true);}
    }
    public static int Main()
    {
        try
        {
            Test("archived computer retains revocation marker",()=>
            {
                using var f=new Fixture();
                TopologyCoordinator.QueuePurgeComputer(f.Cfg,f.OldComputer);
                var pending=f.Pending();
                var old=pending.Desired.Computers.Single(c=>c.Id==f.OldComputer);
                Assert(!old.Active && old.Purged && old.Name=="" && old.JoinedUtc==default,
                    "Revocation marker missing or personal metadata retained");
                Assert(f.Doc.Computers.Single(c=>c.Id==f.OldComputer).Name=="Deleted computer",
                    "Archive purged before shared publication");
                f.CommitPending();
                Assert(!UsageStatistics.Load(f.Cfg).Peers.ContainsKey(f.OldComputer),
                    "Removed peer stats still present");
            });
            Test("cloud purge removes tombstone and saved usage",()=>
            {
                using var f=new Fixture();
                TopologyCoordinator.QueuePurgeCloud(f.Cfg,f.OldCloud);
                var next=f.Pending().Desired;
                Assert(next.PurgedCloudIds.Contains(f.OldCloud) &&
                    !next.RemovedClouds.Any(c=>c.Id==f.OldCloud),"Purge not in queued topology");
                Assert(UsageStatistics.Load(f.Cfg).Self.Clouds.ContainsKey(f.OldCloud),
                    "Statistics erased before topology publish");
                f.CommitPending();
                var clean=UsageStatistics.Load(f.Cfg);
                Assert(!clean.Self.Clouds.ContainsKey(f.OldCloud),"Local archived cloud remains");
                Assert(clean.Peers.Values.All(p=>!p.Clouds.ContainsKey(f.OldCloud)),
                    "Remote stats retained archived cloud");
                Assert(clean.LastPublishedUtc is null,"Cleaned stats publication not advanced");
                Assert(clean.Self.Clouds.ContainsKey(f.ActiveCloud),"Active usage erased");
            });
            Test("late cached snapshot cannot resurrect purged cloud",()=>
            {
                using var f=new Fixture();
                TopologyCoordinator.QueuePurgeCloud(f.Cfg,f.OldCloud);
                f.CommitPending();
                var stored=UsageStatistics.Load(f.Cfg);
                stored.Peers[Guid.NewGuid().ToString("N")]=new UsageSnapshot{Clouds=new()
                { [f.OldCloud]=new UsageCloud{CloudId=f.OldCloud,Name="Stale"}}};
                UsageStatistics.Save(f.Cfg,stored);
                Assert(UsageStatistics.Load(f.Cfg).Peers.Values.All(s=>!s.Clouds.ContainsKey(f.OldCloud)),
                    "Late cloud usage resurrected");
            });
            Test("active computer and cloud cannot be purged",()=>
            {
                using var f=new Fixture();
                var refused=0;
                try{TopologyCoordinator.QueuePurgeComputer(f.Cfg,f.Self);}catch(SyncSafetyException){refused++;}
                try{TopologyCoordinator.QueuePurgeCloud(f.Cfg,f.ActiveCloud);}catch(SyncSafetyException){refused++;}
                Assert(refused==2,"Active object purge not rejected");
            });
            Test("purge refuses pending topology changes",()=>
            {
                using var f=new Fixture();
                TopologyCoordinator.QueuePurgeComputer(f.Cfg,f.OldComputer);
                var refused=false;
                try{TopologyCoordinator.QueuePurgeCloud(f.Cfg,f.OldCloud);}catch(SyncSafetyException){refused=true;}
                Assert(refused,"Second pending edit was permitted");
            });
            Test("usage-only historical cloud can be purged",()=>
            {
                using var f=new Fixture();
                var old=f.Doc.RemovedClouds.Single(c=>c.Id==f.OldCloud);
                f.Doc.RemovedClouds.Remove(old);
                f.WriteTopology(f.Doc);
                TopologyCoordinator.QueuePurgeCloud(f.Cfg,f.OldCloud);
                f.CommitPending();
                Assert(f.Doc.PurgedCloudIds.Contains(f.OldCloud),"Orphan cloud ID not blocked");
                Assert(!UsageStatistics.Load(f.Cfg).Self.Clouds.ContainsKey(f.OldCloud),
                    "Usage-only cloud reappeared");
            });
            Test("duplicate purge not permitted",()=>
            {
                using var f=new Fixture();
                TopologyCoordinator.QueuePurgeCloud(f.Cfg,f.OldCloud);f.CommitPending();
                var refused=false;
                try{TopologyCoordinator.QueuePurgeCloud(f.Cfg,f.OldCloud);}catch(SyncSafetyException){refused=true;}
                Assert(refused,"Repeated deletion accepted");
            });
            Test("queued computer purge is hidden in UI before publication",()=>
            {
                using var f=new Fixture();
                Assert(TopologyCoordinator.ReadPendingPurge(f.Cfg) is null,"Unexpected pending purge");
                TopologyCoordinator.QueuePurgeComputer(f.Cfg,f.OldComputer);
                Assert(TopologyCoordinator.ReadPendingPurge(f.Cfg)?.ComputerId==f.OldComputer,
                    "Computer card did not receive immediate hide hint");
                Assert(UsageStatistics.Load(f.Cfg).Peers.ContainsKey(f.OldComputer),
                    "Statistics were erased before publication");
                f.CommitPending();
                Assert(TopologyCoordinator.ReadPendingPurge(f.Cfg) is null,
                    "Published purge still displayed as pending");
                Assert(f.Doc.Computers.Single(c=>c.Id==f.OldComputer).Purged,
                    "Confirmed computer purge absent from topology");
            });
            Test("queued cloud purge is hidden in UI before publication",()=>
            {
                using var f=new Fixture();
                TopologyCoordinator.QueuePurgeCloud(f.Cfg,f.OldCloud);
                Assert(TopologyCoordinator.ReadPendingPurge(f.Cfg)?.CloudId==f.OldCloud,
                    "Cloud card did not receive immediate hide hint");
                Assert(UsageStatistics.Load(f.Cfg).Self.Clouds.ContainsKey(f.OldCloud),
                    "Cloud statistics were erased before publication");
                f.CommitPending();
                Assert(TopologyCoordinator.ReadPendingPurge(f.Cfg) is null,
                    "Published cloud purge still displayed as pending");
                Assert(!UsageStatistics.Load(f.Cfg).Self.Clouds.ContainsKey(f.OldCloud),
                    "Published cloud purge did not clean statistics");
            });
            Test("pending purge remains visible as pending after local topology commit",()=>
            {
                using var f=new Fixture();
                TopologyCoordinator.QueuePurgeComputer(f.Cfg,f.OldComputer);
                // Simulate a partial publication: local topology already reflects
                // the new revision but pending cannot yet be acknowledged.
                f.WriteTopology(f.Pending().Desired);
                Assert(TopologyCoordinator.ReadPendingPurge(f.Cfg)?.ComputerId==f.OldComputer,
                    "Partially published purge lost its pending indicator");
            });
            Test("unrelated or stale topology edit never hides a card",()=>
            {
                using var f=new Fixture();
                TopologyCoordinator.QueueRenameComputer(f.Cfg,f.Self,"Renamed computer");
                Assert(TopologyCoordinator.ReadPendingPurge(f.Cfg) is null,
                    "A rename unexpectedly hides an archived card");
                File.Delete(TopologyCoordinator.PendingPath(f.Cfg));
                TopologyCoordinator.QueuePurgeCloud(f.Cfg,f.OldCloud);
                var stale=f.Pending();stale.BasedOnChangeId=Guid.NewGuid().ToString("N");
                SyncStorage.SaveJsonAtomic(TopologyCoordinator.PendingPath(f.Cfg),stale,
                    TopologyJson.Default.TopologyPending);
                Assert(TopologyCoordinator.ReadPendingPurge(f.Cfg) is null,
                    "A stale pending edit hid a card");
            });
            Test("unpublished purge cancellation restores archived card",()=>
            {
                using var f=new Fixture();
                TopologyCoordinator.QueuePurgeComputer(f.Cfg,f.OldComputer);
                Assert(TopologyCoordinator.ReadPendingPurge(f.Cfg)?.ComputerId==f.OldComputer,
                    "Pending card not hidden");
                File.Delete(TopologyCoordinator.PendingPath(f.Cfg));
                Assert(TopologyCoordinator.ReadPendingPurge(f.Cfg) is null,
                    "Cancelled pending purge still hides archived card");
                Assert(!f.Doc.Computers.Single(c=>c.Id==f.OldComputer).Purged,
                    "Cancelled purge changed cached topology");
            });
            Console.WriteLine($"[SUMMARY] PurgeTests: {_passed}/13 passed.");
            return 0;
        }
        catch(Exception ex){TestReporter.Unhandled("PurgeTests",ex);return 1;}
    }
}
