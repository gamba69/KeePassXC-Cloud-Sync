using System.Globalization;

namespace KeyPassXCCloudSync;

public sealed record CloudOptions(
    string Name, string Provider, string RemoteDirectory, string BaseUrl,
    string CredentialTarget, string Executable, string RootPath,
    string ClientId, string ClientSecretCredential, string RefreshCredential,
    string AccountLabel, int TimeoutSeconds, int GoogleFolderCacheSeconds=10, string S3Region="auto", int PollSeconds=60, string CloudId="", bool ProtonAutoExecutable=false);

public sealed class AppOptions
{
    public required string ConfigPath { get; init; }
    public required string LocalFile { get; init; }
    public required string StateFile { get; init; }
    public required string DataDirectory { get; init; }
    public required List<CloudOptions> Clouds { get; init; }
    public required bool TrayEnabled { get; init; }
    public required bool SyncEnabled { get; init; }
    public required int PollSeconds { get; init; }
    public required int StatsPublishMinutes { get; init; }
    public required int DebounceMs { get; init; }
    public required int StableMs { get; init; }
    public required int RetrySeconds { get; init; }
    public required int NotifyAfterMinutes { get; init; }
    public required int BackupCopies { get; init; }
    public required int BackupDays { get; init; }
    public required bool BackupOnStartup { get; init; }
    public required int StartupBackupKeep { get; init; }
    public required string LogLevel { get; init; }
    public required int LogSizeMB { get; init; }
    public required int LogFiles { get; init; }

    public static AppOptions Load(string file)
    {
        file=Path.GetFullPath(file);
        if(!File.Exists(file)) throw new FileNotFoundException("Configuration INI not found",file);
        var sections=new Dictionary<string,Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
        string section="";
        foreach(var raw in File.ReadAllLines(file))
        {
            var line=raw.Trim();
            if(line.Length==0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if(line.StartsWith('[') && line.EndsWith(']'))
            {
                section=line[1..^1].Trim();
                if(!sections.TryAdd(section,new(StringComparer.OrdinalIgnoreCase)))
                    throw new FormatException("Duplicate INI section: "+section);
                continue;
            }
            int pos=line.IndexOf('=');
            if(pos<=0 || section.Length==0) throw new FormatException("Malformed INI line");
            if(!sections[section].TryAdd(line[..pos].Trim(),line[(pos+1)..].Trim()))
                throw new FormatException("Duplicate INI option: "+section+"."+line[..pos]);
        }
        string Get(string s,string k,string? otherwise=null)
        {
            if(sections.TryGetValue(s,out var values) && values.TryGetValue(k,out var v) && v.Length>0) return v;
            return otherwise ?? throw new FormatException($"Missing [{s}] {k}");
        }
        int Num(string s,string k,int min,int max,string defaultValue)
        {
            var value=Get(s,k,defaultValue);
            if(!int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out int n) || n<min || n>max)
                throw new FormatException($"Invalid [{s}] {k}: {value}");
            return n;
        }
        bool Boolean(string s,string k,string def)
        {
            var v=Get(s,k,def);
            if(bool.TryParse(v,out var b))return b;
            throw new FormatException($"Invalid boolean [{s}] {k}");
        }
        string Full(string s,string k,string? fallback=null)
        {
            var v=Get(s,k,fallback);
            return Path.GetFullPath(Path.IsPathFullyQualified(v)?v:Path.Combine(Path.GetDirectoryName(file)!,v));
        }
        string SafeRemote(string s)
        {
            var path=Get(s,"RemoteDirectory").Trim('/');
            if(path.Length==0 || path.Split('/').Any(x=>x.Length==0 || x is "." or ".." || x.Contains('\\') || x.Contains(':')))
                throw new FormatException($"Unsafe [{s}] RemoteDirectory");
            return path;
        }
        var clouds=new List<CloudOptions>();
        foreach(var s in sections.Keys.Where(k=>k.StartsWith("Cloud.",StringComparison.OrdinalIgnoreCase)))
        {
            if(!Boolean(s,"Enabled","true"))continue;
            var name=s[6..];
            if(!CloudConnectionName.IsValid(name))
                throw new FormatException("Invalid cloud connection name in ["+s+"]");
            var provider=Get(s,"Provider");
            if(provider is not ("GoogleDrive" or "WebDAV" or "ProtonDrive" or "S3" or "CloudflareR2") &&
               !((provider=="FileSystemTest" || provider=="FileSystemTestNoCas") && Environment.GetEnvironmentVariable("KPS_ENABLE_TEST_STORE")=="1"))
                throw new FormatException($"[{s}] Unknown/disabled provider");
            // Reject unsupported provider settings instead of silently changing the
            // effective remote path or credential semantics. No upgrade rules apply.
            var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {"Enabled","Provider","RemoteDirectory","CloudId","PollSeconds","TimeoutSeconds"};
            var providerKeys=provider switch
            {
                "GoogleDrive" => new[]{"ClientId","ClientSecretCredential","RefreshCredential",
                    "AccountLabel","GoogleFolderCacheSeconds"},
                "ProtonDrive" => new[]{"Executable","AccountLabel"},
                "WebDAV" => new[]{"BaseUrl","CredentialTarget"},
                "S3" or "CloudflareR2" => new[]{"BaseUrl","RootPath","S3Region","CredentialTarget"},
                "FileSystemTest" or "FileSystemTestNoCas" => new[]{"BaseUrl"},
                _ => Array.Empty<string>()
            };
            allowed.UnionWith(providerKeys);
            foreach(var option in sections[s].Keys)
                if(!allowed.Contains(option))
                    throw new FormatException($"[{s}] Unsupported configuration key: {option}. " +
                        "Verify the remote location and create a supported connection configuration.");
            var baseUrl=Get(s,"BaseUrl","");
            var credential=Get(s,"CredentialTarget","");
            var exe=Get(s,"Executable","");
            var autoProtonExe=provider=="ProtonDrive" && exe.Length==0;
            if(exe.Length>0)exe=Full(s,"Executable");
            else if(autoProtonExe)exe=PlatformServices.Current.Paths.ResolveProtonExecutable("",file);
            var root=provider is "S3" or "CloudflareR2"
                ?Get(s,"RootPath",""):provider=="ProtonDrive"?ProtonConfiguration.Root:"";
            // OAuth's built-in ClientId is only a Google Drive default. Other
            // providers must not introduce a phantom shared-topology edit.
            var clientId=Get(s,"ClientId",provider=="GoogleDrive"?GoogleOAuthClientDefaults.ClientId:"");
            var clientSecret=Get(s,"ClientSecretCredential","");
            var refresh=Get(s,"RefreshCredential","KeePassSync/Google/"+name+"/refresh-token");
            var account=provider=="GoogleDrive"?Get(s,"AccountLabel",""):"";
            if(provider is "S3" or "CloudflareR2")
            {
                if(!Uri.TryCreate(baseUrl,UriKind.Absolute,out var s3endpoint) || s3endpoint.Scheme!="https" ||
                    s3endpoint.UserInfo.Length>0 || s3endpoint.Query.Length>0 || s3endpoint.Fragment.Length>0 ||
                    s3endpoint.AbsolutePath!="/" || !S3Store.ValidBucket(root))
                    throw new FormatException($"[{s}] S3 requires HTTPS endpoint, existing bucket and CredentialTarget");
                if(provider=="CloudflareR2" && !baseUrl.Contains(".r2.cloudflarestorage.com",StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"[{s}] Cloudflare R2 endpoint required");
            }
            if(provider=="WebDAV" && (!Uri.TryCreate(baseUrl,UriKind.Absolute,out var uri) || uri.Scheme!="https"))
                throw new FormatException($"[{s}] HTTPS BaseUrl required");
            // Google login is performed from the UI after the cloud is added.
            // Blank account means not yet authorized; publisher OAuth values may be supplied later.
            // A topology-discovered provider may lack local authentication or a CLI path.
            // Mark it NEEDS_AUTH until the user completes local setup.
            // Reject authority-bearing file://host/ URIs for isolated fake clouds.
            // Reject authority-bearing file URIs, which are unsafe for test stores.
            if((provider=="FileSystemTest" || provider=="FileSystemTestNoCas") && (!Uri.TryCreate(baseUrl,UriKind.Absolute,out var u) ||
                !u.IsFile || u.Host.Length!=0 || !Path.IsPathFullyQualified(u.LocalPath)))
                throw new FormatException($"[{s}] FileSystemTest BaseUrl must be a local file:/// URI without host");
            var remote=SafeRemote(s);
            clouds.Add(new(name,provider,remote,baseUrl,credential,exe,root,clientId,clientSecret,refresh,account,
                Num(s,"TimeoutSeconds",5,3600,"120"),Num(s,"GoogleFolderCacheSeconds",0,60,"10"),
                Get(s,"S3Region",provider=="S3"?"us-east-1":"auto"),
                Num(s,"PollSeconds",10,86400,Get("Sync","PollSeconds","60")),
                Get(s,"CloudId",name),autoProtonExe));
        }
        if(clouds.Count==0)throw new FormatException("At least one enabled [Cloud.NAME] is required");
        if(clouds.Select(c=>c.CloudId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=clouds.Count)
            throw new FormatException("CloudId values must be unique");
        var local=Full("General","LocalFile");
        if(!local.EndsWith(".kdbx",StringComparison.OrdinalIgnoreCase))throw new FormatException("LocalFile must be KDBX");
        var state=Full("General","StateFile","data/state.json");
        if(string.Equals(local,state,PlatformServices.Current.Paths.PathComparison))throw new FormatException("StateFile cannot be LocalFile");
        var level=Get("Logging","Level","Info");
        if(level is not ("Info" or "Debug")) throw new FormatException("Logging Level must be Info or Debug");
        return new AppOptions { ConfigPath=file,LocalFile=local,StateFile=state,DataDirectory=Path.GetDirectoryName(state)!,
            Clouds=clouds,TrayEnabled=Boolean("General","TrayEnabled","true"),
            SyncEnabled=Boolean("General","SyncEnabled","false"),
            PollSeconds=Num("Sync","PollSeconds",10,86400,"60"),
            StatsPublishMinutes=Num("Sync","StatsPublishMinutes",1,10080,"60"),DebounceMs=Num("Sync","DebounceMs",0,600000,"1500"),
            StableMs=Num("Sync","StableFileCheckMs",100,60000,"1200"),RetrySeconds=Num("Sync","RetrySeconds",1,3600,"60"),
            NotifyAfterMinutes=Num("Notifications","OfflineAfterMinutes",1,10080,"30"),
            BackupCopies=Num("Backup","MinCopies",20,10000,"20"),BackupDays=Num("Backup","MinDays",30,3650,"30"),
            BackupOnStartup=Boolean("Backup","OnStartup","false"),
            StartupBackupKeep=Num("Backup","KeepStartupCopies",1,999,"30"),
            LogLevel=level,
            LogSizeMB=Num("Logging","MaxSizeMB",1,100,"5"),LogFiles=Num("Logging","MaxFiles",1,100,"5")};
    }

    // Cloud adapters use the read-only transport configuration below.
    // All active clouds are configured through independently named [Cloud.NAME] sections.
    public Config AsTransportConfig(CloudOptions cloud) => new(
        cloud.Provider,LocalFile,cloud.RemoteDirectory,StateFile,DebounceMs,StableMs,PollSeconds,
        24,RetrySeconds,1,RetrySeconds,cloud.TimeoutSeconds,BackupCopies,BackupCopies,BackupDays,BackupDays,
        cloud.BaseUrl,cloud.CredentialTarget,
        cloud.Provider=="ProtonDrive"?PlatformServices.Current.Paths.ResolveProtonExecutable(cloud.Executable,ConfigPath):cloud.Executable,
        cloud.ClientId,cloud.ClientSecretCredential,
        cloud.RefreshCredential,cloud.AccountLabel,cloud.RootPath,
        LogLevel,LogSizeMB,LogFiles,false,cloud.GoogleFolderCacheSeconds,cloud.S3Region);
}
