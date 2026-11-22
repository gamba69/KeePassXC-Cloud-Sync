using KeyPassXCCloudSync;

// HEADLESS protocol regression tests. They cannot access real clouds or a working vault.
internal static class Program
{
    private static string _testProtonBinary="";
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
        public Task<bool> StartProtonLoginAsync(string executable,CancellationToken cancellationToken)
            =>throw new NotSupportedException("No subprocesses in tests");
    }
    private sealed class LoginProcess(bool succeeds) : IPlatformProcessLauncher
    {
        internal int Starts;
        public void OpenFolder(string path)=>throw new NotSupportedException();
        public Task<bool> StartProtonLoginAsync(string executable,CancellationToken cancellationToken)
        {
            Starts++;
            if(executable!=_testProtonBinary)throw new Exception("Unexpected Proton binary");
            return Task.FromResult(succeeds);
        }
    }
    private sealed class LoginCli : IPlatformCliRunner
    {
        internal int Probes;
        internal Exception? Failure;
        internal string Output="[]";
        public Task<string> RunProtonAsync(string executable,int timeoutSeconds,
            CancellationToken cancellationToken,params string[] arguments)
        {
            Probes++;
            if(executable!=_testProtonBinary || timeoutSeconds!=37 ||
               !arguments.SequenceEqual(new[]{"filesystem","list","/my-files","--json"}))
                throw new Exception("Proton session probe must list only /my-files");
            if(Failure is not null)throw Failure;
            return Task.FromResult(Output);
        }
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
    private static void VerifyUsagePeriods()
    {
        // Sunday 22 Nov: current week starts Monday 16 Nov, month on 1 Nov.
        // Explicit reference date keeps the test stable across time zones/midnight.
        var today=new DateTime(2026,11,22);
        var cloud=new UsageCloud
        {
            Total=new V2OperationTotals{Get=31,DownloadBytes=31*1024},
            Days=new Dictionary<string,V2OperationTotals>
            {
                ["2026-11-22"]=new(){Get=1,DownloadBytes=1024},
                ["2026-11-21"]=new(){Get=2,DownloadBytes=2*1024},
                ["2026-11-16"]=new(){Get=4,DownloadBytes=4*1024},
                ["2026-11-01"]=new(){Get=8,DownloadBytes=8*1024},
                ["2026-10-31"]=new(){Get=16,DownloadBytes=16*1024},
                // Future or malformed keys must never be counted in a period.
                ["2026-11-23"]=new(){Get=64},
                ["invalid-date"]=new(){Get=128}
            }
        };
        foreach(var (period,expected) in new[]
        {
            ("today",1L),("week",7L),("month",15L),("all",31L)
        })
        {
            var totals=UsageStatistics.Period(cloud,period,today);
            if(totals.Get!=expected || totals.DownloadBytes!=expected*1024)
                throw new Exception("Incorrect "+period+" daily statistics boundary");
        }
        if(UsageStatistics.Period(cloud,"today",today.AddDays(1)).Get!=64)
            throw new Exception("Today must follow the selected local calendar date");
        if(UsageStatistics.Period(cloud,"today",today.AddDays(-30)).Get!=0)
            throw new Exception("An empty date must have zero daily statistics");
        try
        {
            UsageStatistics.Period(cloud,"unexpected",today);
            throw new Exception("An unsupported period silently returned another period");
        }
        catch(ArgumentOutOfRangeException){}
    }
    private static void VerifyProtonLogin()
    {
        _testProtonBinary=Path.Combine(Path.GetTempPath(),"kpcs-proton-test-"+
            Guid.NewGuid().ToString("N")+".exe");
        File.WriteAllBytes(_testProtonBinary,[]);
        try
        {
            bool Login(IPlatformProcessLauncher process,IPlatformCliRunner cli)
                =>CloudStoreFactory.LoginProtonAsync(_testProtonBinary,37,process,cli,
                    CancellationToken.None).GetAwaiter().GetResult();
            var launched=new LoginProcess(true);
            var cli=new LoginCli();
            if(!Login(launched,cli) || launched.Starts!=1 || cli.Probes!=1)
                throw new Exception("Package Proton login must verify a read-only CLI listing");
            var stopped=new LoginProcess(false);
            var uncalled=new LoginCli();
            if(Login(stopped,uncalled) || uncalled.Probes!=0)
                throw new Exception("Canceled browser login must not probe Proton Drive");
            var authFailure=new LoginCli
            {
                Failure=new KeePassXC.CloudTransport.CloudTransportException("PROTON_AUTH",
                    KeePassXC.CloudTransport.CloudFailureKind.Authentication,"login required")
            };
            // A rejected probe is immediately unauthenticated. LoginAsync intentionally
            // retries it for up to two minutes, so test the probe without that wait.
            using(var authProbe=KeePassXC.CloudTransport.CloudTransportFactory.Create(
                new KeePassXC.CloudTransport.CloudConnectionOptions
                {
                    Provider=KeePassXC.CloudTransport.CloudProvider.ProtonDrive,
                    ProtonCliExecutable=_testProtonBinary,
                    Timeout=TimeSpan.FromSeconds(37)
                },CloudStoreFactory.Services(null,new LoginProcess(true),authFailure)))
            {
                if(authProbe.CheckConnectionAsync(CancellationToken.None).GetAwaiter().GetResult())
                    throw new Exception("Proton authentication rejection must not confirm a session");
            }
            try
            {
                _=Login(new LoginProcess(true),new LoginCli{Output="not json"});
                throw new Exception("Malformed Proton listing was accepted");
            }
            catch(System.Text.Json.JsonException){}
            try
            {
                _=Login(new LoginProcess(true),new LoginCli{
                    Failure=new IOException("Temporary connection failure")});
                throw new Exception("Transient Proton failure was concealed");
            }
            catch(IOException){}
            if(ProtonCliMaintenance.TryBeginUpdate() is not { } update)
                throw new Exception("Proton login failed to release the package's CLI lease");
            update.Dispose();
        }
        finally { File.Delete(_testProtonBinary); }
    }
    private static int Main()
    {
        PlatformServices.Configure(new Services());
        TestReporter.Run("ProtocolTests","Proton browser login verification",VerifyProtonLogin);
        TestReporter.Run("ProtocolTests","per-cloud telemetry",VerifyPerCloudTelemetry);
        TestReporter.Run("ProtocolTests","daily/week/month/all periods",VerifyUsagePeriods);
        V2Diagnostics.Progress=Console.WriteLine;
        var local=V2Diagnostics.Run();
        if(local!=0)return local;
        return V2Diagnostics.RunSafety();
    }
}
