using KeyPassXCCloudSync;

// Headless regression tests: use only temporary INI files and an inert platform.
internal static class Program
{
    private sealed class TestSecrets : ISecretStore
    {
        public (string User, string Password) Read(string target) => throw new NotSupportedException();
        public void Write(string target, string user, string secret) => throw new NotSupportedException();
        public bool Exists(string target) => false;
        public void RemoveUnused(IReadOnlyCollection<string> active, bool fullLeave=false) { }
    }
    private sealed class TestAutostart : IAutostartService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled, string configPath) { }
    }
    private sealed class TestReservation : IDatabaseReservation
    {
        public void Dispose() { }
    }
    private sealed class TestLocks : IDatabaseLockService
    {
        public IDatabaseReservation Acquire(string lockName) => new TestReservation();
    }
    private sealed class TestPaths : IPlatformPaths
    {
        public StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;
        public string MachineName => "test-device";
        public string ResolveConfig(string? explicitConfig) => explicitConfig ?? "";
        public string DefaultDataDirectory(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "data");
        public string LocalUiFile(string configPath, string fileName) => Path.Combine(Path.GetDirectoryName(configPath)!, fileName);
        public string ResolveProtonExecutable(string? configured,string? configPath=null) =>
            TestProtonCli.Resolve(configured,configPath);
        public string StartupLogPath => Path.Combine(Path.GetTempPath(), "kpcs-cloudname-test.log");
    }
    private sealed class TestBrowser : IExternalBrowser
    {
        public void Open(string url) => throw new NotSupportedException();
    }
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
    private sealed class TestPlatform : IPlatformServices
    {
        public ISecretStore Secrets { get; } = new TestSecrets();
        public IPlatformChangeMonitoring Changes {get;}=new TestChanges();
        public IPlatformCliRunner Cli {get;}=new TestCli();
        public IAutostartService Autostart { get; } = new TestAutostart();
        public IDatabaseLockService DatabaseLocks { get; } = new TestLocks();
        public IPlatformPaths Paths { get; } = new TestPaths();
        public IPlatformProcessLauncher Processes {get;}=new TestProcesses();
        public IExternalBrowser Browser { get; } = new TestBrowser();
    }

    private static void Check(bool condition, string description)
    {
        TestReporter.Check("CloudNameTests",description,condition);
    }

    private static TopologyDocument Topology(string label) => new()
    {
        TopologyId=Guid.NewGuid().ToString("N"),
        ChangeId=Guid.NewGuid().ToString("N"),
        Revision=1,
        Clouds=[new TopologyCloud
        {
            Id=Guid.NewGuid().ToString("N"),
            Name=label,
            Provider="WebDAV",
            RemoteDirectory="KeePassSync",
            PollSeconds=60
        }]
    };

    private static void Main()
    {
        PlatformServices.Configure(new TestPlatform());
        var good = new[] { "Home Assistant", "Домашнее облако", "Cloudflare R2 (Home)",
            "My.Cloud_2", "Backup + Storage", "Google-Work", "S3 & WebDAV", "ABC123" };
        foreach (var label in good)
        {
            Check(CloudConnectionName.IsValid(label), "valid display name: " + label);
            TestReporter.Run("CloudNameTests","topology accepts display name: "+label,
                ()=>TopologyCoordinator.Validate(Topology(label)));
        }
        TestReporter.Pass("CloudNameTests","shared topology accepts valid display names");

        var bad = new[] { "", " ", " Leading", "Trailing ", "[Cloud]", "a]b", "a=b", "a/b",
            "a\\b", "a\nb", "a\rb", "a\tb", "....", new string('a',81) };
        foreach (var label in bad)
        {
            Check(!CloudConnectionName.IsValid(label), "invalid display name rejected: " + label.Replace("\n", "\\n").Replace("\r", "\\r"));
            var failed=false;
            try { TopologyCoordinator.Validate(Topology(label)); }
            catch (SyncSafetyException) { failed=true; }
            Check(failed, "topology rejects invalid name");
        }

        var root=Path.Combine(Path.GetTempPath(),"kpcs-cloudnames-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ini=Path.Combine(root,"KeyPassXCCloudSync.ini");
            var id=Guid.NewGuid().ToString("N");
            var draft=new SettingsDraft
            {
                LocalFile=Path.Combine(root,"vault.kdbx"),
                DataDirectory=Path.Combine(root,"data"),
                Clouds=[new CloudDraft
                {
                    Name="Home Assistant",Provider="WebDAV",Remote="KeePassSync",
                    BaseUrl="https://example.org/dav/",CredentialTarget="KPCS/Test/NoSecret",CloudId=id
                }]
            };
            File.WriteAllText(ini,SettingsStore.Ini(draft));
            var first=AppOptions.Load(ini);
            Check(first.Clouds.Single().Name=="Home Assistant", "INI round-trip preserves spaces");
            Check(first.Clouds.Single().CloudId==id,"INI round-trip preserves CloudId");
            draft.Clouds.Single().Name="Домашнее облако";
            File.WriteAllText(ini,SettingsStore.Ini(draft));
            var second=AppOptions.Load(ini);
            Check(second.Clouds.Single().Name=="Домашнее облако", "INI round-trip preserves Unicode");
            Check(second.Clouds.Single().CloudId==id, "rename preserves CloudId");
            draft.Clouds.Add(draft.Clouds[0].Clone());
            draft.Clouds[1].Name="домашнее облако";
            var duplicateRejected=false;
            try { _=SettingsStore.Ini(draft); }
            catch (ArgumentException) { duplicateRejected=true; }
            Check(duplicateRejected,"case-insensitive duplicate labels rejected");

            // Proton uses a fixed internal root and the system-managed CLI cache.
            var protonId=Guid.NewGuid().ToString("N");
            draft.Clouds=[new CloudDraft
            {
                Name="Proton",Provider="ProtonDrive",Remote="KeePassSync",
                Account="",CloudId=protonId
            }];
            var cleanProtonIni=SettingsStore.Ini(draft);
            Check(!cleanProtonIni.Contains("RootPath =",StringComparison.Ordinal),
                "fresh Proton config has no RootPath");
            Check(!cleanProtonIni.Contains("CacheDirectory =",StringComparison.Ordinal),
                "fresh Proton config uses standard CLI cache");
            Check(!cleanProtonIni.Contains("Executable =",StringComparison.Ordinal),
                "fresh Proton config uses CLI autodiscovery");
            Check(!cleanProtonIni.Contains("AccountLabel =",StringComparison.Ordinal),
                "Proton account label is not written to the INI");
            File.WriteAllText(ini,cleanProtonIni);
            File.AppendAllText(ini,"\nRootPath = /my-files/other-location\n");
            var unsupportedRejected=false;
            try { _=AppOptions.Load(ini); }
            catch(FormatException ex) when(ex.Message.Contains("Unsupported configuration key",StringComparison.Ordinal))
            { unsupportedRejected=true; }
            Check(unsupportedRejected,"unsupported Proton settings cannot silently change the remote path");
            File.WriteAllText(ini,cleanProtonIni);
            var freshProton=AppOptions.Load(ini).Clouds.Single();
            Check(freshProton.RootPath=="/my-files" && freshProton.RemoteDirectory=="KeePassSync",
                "fresh Proton config resolves the CLI root internally");
            File.AppendAllText(ini,"\nAccountLabel = unused@example.invalid\n");
            var withUnusedLabel=AppOptions.Load(ini).Clouds.Single();
            Check(withUnusedLabel.AccountLabel.Length==0 &&
                withUnusedLabel.RemoteDirectory==freshProton.RemoteDirectory,
                "unused Proton account label does not alter remote location or require sign-in");
            File.WriteAllText(ini,cleanProtonIni);
            var fakeCli=Path.Combine(root,"proton-drive.exe");
            File.WriteAllText(fakeCli,""); // discovery test: never execute it
            File.WriteAllText(ini,cleanProtonIni);
            var autoExe=new TestPaths().ResolveProtonExecutable("",ini);
            Check(autoExe==fakeCli,"CLI autodiscovery checks the INI folder");
            var cfg=AppOptions.Load(ini);
            Check(cfg.AsTransportConfig(cfg.Clouds.Single()).ProtonExecutable==fakeCli,
                "Proton transport receives the automatically discovered CLI");
            Check(cfg.Clouds.Single().ProtonAutoExecutable &&
                SettingsDraft.Load(ini).Clouds.Single().Executable=="",
                "autodetected CLI path remains blank in user config form");
            Check(TopologyCoordinator.IsAuthorized(cfg.Clouds.Single()),
                "installed Proton CLI does not require an arbitrary account label");
            var toolsDir=Path.Combine(root,"tools");
            Directory.CreateDirectory(toolsDir);
            var toolsCli=Path.Combine(toolsDir,"proton-drive.exe");
            File.Move(fakeCli,toolsCli);
            Check(new TestPaths().ResolveProtonExecutable("",ini)==toolsCli,
                "portable tools/proton-drive.exe is detected automatically");
            Check(new TestPaths().ResolveProtonExecutable(fakeCli,ini)==fakeCli,
                "explicit CLI path overrides autodiscovery");
            File.Delete(toolsCli);


        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine("[SUMMARY] CloudNameTests completed.");
    }
}
