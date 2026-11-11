using KeyPassXCCloudSync;

// HEADLESS protocol regression tests. They cannot access real clouds or a working vault.
internal static class Program
{
    private sealed class Secrets : ISecretStore
    {
        public (string User,string Password) Read(string target)=>throw new SyncSafetyException("No test secret: "+target);
        public void Write(string target,string user,string secret)=>throw new SyncSafetyException("No real credentials in tests");
        public bool Exists(string target)=>false;
        public void RemoveUnused(IReadOnlyCollection<string> active,bool fullLeave=false){}
    }
    private sealed class Autostart : IAutostartService
    {
        public bool IsEnabled()=>false;
        public void SetEnabled(bool enabled,string configPath){}
    }
    private sealed class Reservation : IDatabaseReservation {public void Dispose(){}}
    private sealed class Locks : IDatabaseLockService
    {public IDatabaseReservation Acquire(string lockName)=>new Reservation();}
    private sealed class Paths : IPlatformPaths
    {
        public StringComparison PathComparison=>StringComparison.OrdinalIgnoreCase;
        public string MachineName => "test-device";
        public string ResolveConfig(string? explicitConfig)=>explicitConfig??"";
        public string DefaultDataDirectory(string configPath)=>Path.Combine(Path.GetDirectoryName(configPath)!,"data");
        public string LocalUiFile(string configPath,string fileName)=>Path.Combine(Path.GetDirectoryName(configPath)!,fileName);
        public string ResolveProtonExecutable(string? configured,string? configPath=null)=>
            TestProtonCli.Resolve(configured,configPath);
        public string StartupLogPath=>Path.Combine(Path.GetTempPath(),"kpcs-tests-startup.log");
    }
    private sealed class Browser : IExternalBrowser
    {public void Open(string url)=>throw new InvalidOperationException("No browser in tests");}
    private sealed class TestProcesses : IPlatformProcessLauncher
    {
        public void OpenFolder(string path)=>throw new NotSupportedException("No OS shell in tests");
        public void StartProtonLogin(string executable)
            =>throw new NotSupportedException("No subprocesses in tests");
    }
    private sealed class NoopDisposable : IDisposable {public void Dispose(){} }
    private sealed class TestChanges : IPlatformChangeMonitoring
    {
        public IDisposable WatchFile(string fullFilePath,Action changed)=>new NoopDisposable();
        public IDisposable WatchNetwork(Action changed)=>new NoopDisposable();
    }
    private sealed class TestCli : IPlatformCliRunner
    {
        public Task<string> RunProtonAsync(string executable,int timeoutSeconds,
            CancellationToken cancellationToken,params string[] arguments)=>throw new NotSupportedException();
    }
    private static class TestProtonCli
    {
        internal static string Resolve(string? configured,string? configPath)
        {
            if(!string.IsNullOrWhiteSpace(configured))return Path.GetFullPath(configured);
            if(configPath is null)return "";
            var folder=Path.GetDirectoryName(Path.GetFullPath(configPath))!;
            foreach(var relative in new[]{"proton-drive.exe",Path.Combine("tools","proton-drive.exe")})
            {
                var candidate=Path.Combine(folder,relative);
                if(File.Exists(candidate))return candidate;
            }
            return "";
        }
    }
    private sealed class Services : IPlatformServices
    {
        public ISecretStore Secrets {get;}=new Secrets();
        public IPlatformChangeMonitoring Changes {get;}=new TestChanges();
        public IPlatformCliRunner Cli {get;}=new TestCli();
        public IAutostartService Autostart {get;}=new Autostart();
        public IDatabaseLockService DatabaseLocks {get;}=new Locks();
        public IPlatformPaths Paths {get;}=new Paths();
        public IPlatformProcessLauncher Processes {get;}=new TestProcesses();
        public IExternalBrowser Browser {get;}=new Browser();
    }
    private static void VerifyPerCloudTelemetry()
    {
        var empty=new V2OperationTotals();
        if(empty.MedianMilliseconds is not null || empty.P95Milliseconds is not null ||
           empty.DownloadBytesPerSecond is not null)
            throw new Exception("No data must not be converted to zero latency/bandwidth");
        var first=new V2OperationTotals{Get=2,Put=1,DownloadBytes=1024,
            DownloadMilliseconds=1000,UploadBytes=2048,UploadMilliseconds=500};
        first.Observe(25,true);
        first.Observe(120,true);
        first.Observe(300,false);
        if(first.Operations!=3 || first.Failed!=1 ||
           first.SuccessfulSamples!=2 || first.MedianMilliseconds!=72.5 ||
           first.P95Milliseconds!=120 || first.DownloadBytesPerSecond!=1024 ||
           first.UploadBytesPerSecond!=4096)
            throw new Exception("Cloud telemetry exact percentile/throughput is inconsistent");
        var second=first.Copy();
        second.Observe(400,true);
        first.Add(second);
        if(first.SuccessfulSamples!=5 || first.MedianMilliseconds!=120 ||
           first.P95Milliseconds!=400 ||
           first.Failed!=2 || first.Operations!=6)
            throw new Exception("Cloud telemetry merge is inconsistent");
        // Even-count median is the exact average of the central durations.
        var middle=new V2OperationTotals();
        middle.Observe(10,true);
        middle.Observe(70,true);
        middle.Observe(300,true);
        middle.Observe(700,true);
        if(middle.MedianMilliseconds!=185 || middle.P95Milliseconds!=700)
            throw new Exception("Exact median or P95 nearest rank is incorrect");
        // A historical JSON payload with no exact samples must yield no median.
        if(new V2OperationTotals{LatencyMilliseconds=null!}.MedianMilliseconds is not null)
            throw new Exception("Missing exact samples must not fabricate a median");
        // Source-generated JSON must preserve the exact millisecond keys,
        // including the half-millisecond result of an even sample count.
        var snapshot=new UsageSnapshot{Clouds=new()
        {
            ["cloud-1"]=new UsageCloud{CloudId="cloud-1",Total=first.Copy()}
        }};
        var serialized=System.Text.Json.JsonSerializer.Serialize(snapshot,UsageJson.Default.UsageSnapshot);
        var restored=System.Text.Json.JsonSerializer.Deserialize(serialized,UsageJson.Default.UsageSnapshot);
        if(restored is null || restored.Clouds["cloud-1"].Total.MedianMilliseconds!=120 ||
           restored.Clouds["cloud-1"].Total.P95Milliseconds!=400 ||
           serialized.Contains("LatencyBuckets",StringComparison.Ordinal))
            throw new Exception("Exact latency observations or compact JSON format did not survive roundtrip");
        // An older saved document can contain an unused interval field, but
        // exact durations already present in the same document must survive.
        var withOldField=serialized.Replace("\"LatencyMilliseconds\":",
            "\"LatencyBuckets\":[1,2,3],\"LatencyMilliseconds\":",StringComparison.Ordinal);
        var compatible=System.Text.Json.JsonSerializer.Deserialize(withOldField,
            UsageJson.Default.UsageSnapshot);
        if(compatible?.Clouds["cloud-1"].Total.P95Milliseconds!=400)
            throw new Exception("Previously stored stats lost their exact durations");
        // Nearest rank: the 19th of 20 observations, then the 20th of 21.
        var ranked=new V2OperationTotals();
        for(var i=0;i<18;i++)ranked.Observe(10,true);
        ranked.Observe(200,true);
        ranked.Observe(400,true);
        if(ranked.SuccessfulSamples!=20 || ranked.P95Milliseconds!=200)
            throw new Exception("P95 rank for 20 samples is wrong");
        ranked.Observe(500,true);
        if(ranked.P95Milliseconds!=400)
            throw new Exception("P95 rank for 21 samples is wrong");
        var one=new V2OperationTotals();
        one.Observe(123,true);
        if(one.P95Milliseconds!=123 || one.MedianMilliseconds!=123)
            throw new Exception("Single successful operation must determine both percentiles");
        var slow=new V2OperationTotals();
        slow.Observe(300000,true);
        if(slow.P95Milliseconds!=300000)
            throw new Exception("Exact percentile must not be capped by an interval bound");
        var legacy=new UsageLocal
        {
            Self=new UsageSnapshot{SchemaVersion=1,Clouds=new()
            {
                ["cloud-1"]=new UsageCloud{CloudId="cloud-1",Total=first.Copy(),
                    Days=new(){{"2026-11-11",first.Copy()}}}
            }},
            Peers=new(){{"old-peer",new UsageSnapshot{SchemaVersion=1,Clouds=new()
            {
                ["cloud-1"]=new UsageCloud{CloudId="cloud-1",Total=first.Copy()}
            }}}},
            LastPublishedUtc=DateTimeOffset.UtcNow
        };
        var oldCount=legacy.Self.Clouds["cloud-1"].Total.Operations;
        var oldBytes=legacy.Self.Clouds["cloud-1"].Total.DownloadBytes;
        var changed=UsageStatistics.EnsureExactLatency(legacy);
        if(!changed || legacy.Self.SchemaVersion!=2 ||
           legacy.Self.Clouds["cloud-1"].Total.Operations!=oldCount ||
           legacy.Self.Clouds["cloud-1"].Total.DownloadBytes!=oldBytes ||
           legacy.Self.Clouds["cloud-1"].Total.MedianMilliseconds is not null ||
           legacy.Self.Clouds["cloud-1"].Days["2026-11-11"].MedianMilliseconds is not null ||
           !legacy.Peers.ContainsKey("old-peer") ||
           legacy.Peers["old-peer"].Clouds["cloud-1"].Total.Operations!=oldCount ||
           legacy.Peers["old-peer"].Clouds["cloud-1"].Total.MedianMilliseconds is not null ||
           legacy.LastPublishedUtc is not null)
            throw new Exception("Statistics transition did not isolate old latency data");
        if(legacy.Self.SchemaVersion!=2 ||
           legacy.Self.Clouds["cloud-1"].Total.MedianMilliseconds is not null)
            throw new Exception("Statistics transition must be stable after repeated loading");
    }
    private static int Main()
    {
        TestReporter.Run("ProtocolTests","per-cloud telemetry",VerifyPerCloudTelemetry);
        PlatformServices.Configure(new Services());
        V2Diagnostics.Progress=Console.WriteLine;
        var local=V2Diagnostics.Run();
        if(local!=0)return local;
        return V2Diagnostics.RunSafety();
    }
}
