using KeePassXC.CloudTransport;
using PackageFailure = KeePassXC.CloudTransport.CloudFailureKind;
using PackageException = KeePassXC.CloudTransport.CloudTransportException;

namespace KeyPassXCCloudSync;

// The application owns replication and its wire protocol; this adapter owns only
// compatibility with the package's object operations and host service contracts.
internal static class CloudStoreFactory
{
    internal static CloudConnectionOptions Options(AppOptions settings, CloudOptions cloud)
    {
        var c=settings.AsTransportConfig(cloud);
        var provider=cloud.Provider switch
        {
            "GoogleDrive" => CloudProvider.GoogleDrive,
            "ProtonDrive" => CloudProvider.ProtonDrive,
            "WebDAV" => CloudProvider.WebDav,
            "S3" => CloudProvider.S3,
            "CloudflareR2" => CloudProvider.CloudflareR2,
            _ => throw new NotSupportedException("Not a production cloud provider: "+cloud.Provider)
        };
        return new CloudConnectionOptions
        {
            Provider=provider,
            // The existing v2 engine already includes RemoteDirectory in each key.
            // Adding that prefix here would change the remote protocol location.
            RemotePrefix="",
            Endpoint=provider is CloudProvider.S3 or CloudProvider.CloudflareR2 or CloudProvider.WebDav
                ?c.WebDavUrl:null,
            Bucket=provider is CloudProvider.S3 or CloudProvider.CloudflareR2 ?c.ProtonRootPath:null,
            S3Region=c.S3Region,
            CredentialTarget=c.WebDavCredentialTarget,
            Timeout=TimeSpan.FromSeconds(c.OperationTimeoutSec),
            GoogleClientId=c.GoogleClientId,
            GoogleClientSecret=string.Equals(c.GoogleClientId,GoogleOAuthClientDefaults.ClientId,
                StringComparison.Ordinal)?GoogleOAuthClientDefaults.ClientSecret:null,
            GoogleClientSecretTarget=string.IsNullOrWhiteSpace(c.GoogleClientSecretCredential)
                ?null:c.GoogleClientSecretCredential,
            GoogleRefreshTokenTarget=c.GoogleRefreshCredential,
            GoogleExpectedAccount=string.IsNullOrWhiteSpace(c.GoogleAccountLabel)
                ?null:c.GoogleAccountLabel,
            GoogleFolderCacheSeconds=c.GoogleFolderCacheSeconds,
            ProtonCliExecutable=c.ProtonExecutable
        };
    }

    internal static CloudTransportServices Services(Action<string>? progress=null,
        IPlatformProcessLauncher? processes=null,IPlatformCliRunner? cli=null)
    {
        var host=PlatformServices.Current;
        return new CloudTransportServices
        {
            Secrets=new SecretBridge(host.Secrets),
            Browser=new BrowserBridge(host.Browser),
            Cli=new CliBridge(cli??host.Cli),
            ProtonLogin=new ProtonLoginBridge(processes??host.Processes),
            Events=progress is null?null:message=>progress(message.Stage)
        };
    }

    internal static IRemoteStore Create(AppOptions settings,CloudOptions cloud,Action<string>? progress=null)
    {
        try
        {
            var transport=CloudTransportFactory.Create(Options(settings,cloud),Services(progress));
            // This interface is optional: no client-side HEAD-then-PUT emulation.
            return transport is IConditionalCloudTransport conditional &&
                transport.Capabilities.AtomicServerCompareAndSwap
                ?new ConditionalCloudStoreAdapter(transport,conditional)
                :new CloudStoreAdapter(transport);
        }
        catch(PackageException e){throw Convert(e);}
        catch(CloudSafetyException e){throw Convert(e);}
    }

    internal static async Task<string> LoginGoogleAsync(AppOptions settings,CloudOptions cloud,CancellationToken ct)
    {
        var options=Options(settings,cloud);
        using var http=new HttpClient{Timeout=options.Timeout};
        try{return await new GoogleOAuthSession(options,Services(),http).LoginAsync(ct);}
        catch(PackageException e){throw Convert(e);}
        catch(CloudSafetyException e){throw Convert(e);}
    }

    internal static Task<bool> LoginProtonAsync(string executable,int seconds,CancellationToken ct)
        =>LoginProtonAsync(executable,seconds,PlatformServices.Current.Processes,
            PlatformServices.Current.Cli,ct);

    internal static async Task<bool> LoginProtonAsync(string executable,int seconds,
        IPlatformProcessLauncher processes,IPlatformCliRunner cli,CancellationToken ct)
    {
        var options=new CloudConnectionOptions
        {
            Provider=CloudProvider.ProtonDrive,ProtonCliExecutable=executable,
            Timeout=TimeSpan.FromSeconds(seconds),RemotePrefix=""
        };
        try
        {
            using var transport=CloudTransportFactory.Create(options,Services(null,processes,cli));
            return await ((ICloudAuthentication)transport).LoginAsync(ct);
        }
        catch(PackageException e){throw Convert(e);}
        catch(CloudSafetyException e){throw Convert(e);}
    }

    internal static CloudTransportException Convert(PackageException e)
        =>new(e.Code,e.Kind switch
        {
            PackageFailure.Authentication=>CloudFailureKind.Authentication,
            PackageFailure.Configuration=>CloudFailureKind.Configuration,
            PackageFailure.RateLimited=>CloudFailureKind.RateLimited,
            PackageFailure.Transient=>CloudFailureKind.Transient,
            PackageFailure.ConcurrentWrite or PackageFailure.Safety=>CloudFailureKind.Safety,
            _=>CloudFailureKind.Unknown
        },e.Message);

    internal static SyncSafetyException Convert(CloudSafetyException e)
    {
        // Keep the old read-only WebDAV fallback when a server lacks strong ETags.
        if(e.Message.Contains("WebDAV",StringComparison.OrdinalIgnoreCase) &&
           e.Message.Contains("strong ETag",StringComparison.OrdinalIgnoreCase))
            return new SyncSafetyException("WebDAV did not provide a strong ETag");
        return new SyncSafetyException(e.Message);
    }

    internal static bool ValidBucket(string name)
        =>name.Length is >=3 and <=63 && name[0] is not ('.' or '-') &&
          name[^1] is not ('.' or '-') &&
          !name.Contains("..",StringComparison.Ordinal) &&
          !name.Contains(".-",StringComparison.Ordinal) &&
          !name.Contains("-.",StringComparison.Ordinal) &&
          name.All(c=>char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-');

    internal static bool ValidRegion(string region)
        =>region.Length is >0 and <=64 &&
          region.All(c=>char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c=='-');

    private sealed class SecretBridge(ISecretStore store):ICloudSecretStore
    {
        public (string User,string Password) Read(string target)
        {
            try{return store.Read(target);}
            catch(SyncSafetyException)
            {
                throw new PackageException("CREDENTIALS_MISSING",PackageFailure.Configuration,
                    "Cloud credential is missing or invalid in the Windows credential store");
            }
        }
        public void Write(string target,string user,string secret)=>store.Write(target,user,secret);
    }
    private sealed class BrowserBridge(IExternalBrowser browser):ICloudBrowserLauncher
    {public void Open(string url)=>browser.Open(url);}
    private sealed class CliBridge(IPlatformCliRunner runner):ICloudCliRunner
    {
        public async Task<string> RunAsync(string executable,TimeSpan timeout,CancellationToken ct,
            params string[] args)
        {
            try{return await runner.RunProtonAsync(executable,
                Math.Clamp((int)Math.Ceiling(timeout.TotalSeconds),1,3600),ct,args);}
            catch(CloudTransportException e)
            {
                throw new PackageException(e.Code,e.Kind switch
                {
                    CloudFailureKind.Authentication=>PackageFailure.Authentication,
                    CloudFailureKind.Configuration=>PackageFailure.Configuration,
                    CloudFailureKind.RateLimited=>PackageFailure.RateLimited,
                    CloudFailureKind.Transient=>PackageFailure.Transient,
                    CloudFailureKind.Safety=>PackageFailure.Safety,
                    _=>PackageFailure.Unknown
                },e.Message,e);
            }
        }
    }
    private sealed class ProtonLoginBridge(IPlatformProcessLauncher launcher):IProtonLoginLauncher
    {public Task<bool> StartLoginAsync(string executable,CancellationToken ct)
        =>launcher.StartProtonLoginAsync(executable,ct);}
}

internal class CloudStoreAdapter(ICloudTransport transport):IRemoteStore
{
    protected readonly ICloudTransport Transport=transport;

    protected static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try{return await action();}
        catch(PackageException e){throw CloudStoreFactory.Convert(e);}
        catch(CloudSafetyException e){throw CloudStoreFactory.Convert(e);}
    }
    protected static async Task Guard(Func<Task> action)
    {
        try{await action();}
        catch(PackageException e){throw CloudStoreFactory.Convert(e);}
        catch(CloudSafetyException e){throw CloudStoreFactory.Convert(e);}
    }
    public Task<bool> DownloadAsync(string path,string destination,CancellationToken ct)
        =>Guard(()=>Transport.DownloadAsync(path,destination,ct));
    public Task UploadAsync(string source,string path,WriteMode mode,CancellationToken ct)
        =>Guard(()=>Transport.UploadAsync(source,path,
            mode==WriteMode.CreateNew?CloudWriteMode.CreateNew:CloudWriteMode.Replace,ct));
    public Task<IReadOnlyList<RemoteFile>> ListAsync(string directory,CancellationToken ct)
        =>Guard(async()=> (IReadOnlyList<RemoteFile>)(await Transport.ListAsync(directory,ct))
            .Select(x=>new RemoteFile(x.Path,x.ModifiedUtc)).ToArray());
    public Task DeleteAsync(string path,CancellationToken ct)
        =>Guard(()=>Transport.DeleteAsync(path,ct));
    public Task EnsureDirectoryAsync(string directory,CancellationToken ct)
        =>Guard(()=>Transport.EnsureDirectoryAsync(directory,ct));
    public async Task DeleteTestDirectoryAsync(string directory,CancellationToken ct)
    {
        if(!IntegrationIsolation.IsTestRunRoot(directory))
            throw new SyncSafetyException("Refusing to delete a non-isolated test directory");
        // S3 prefixes are virtual. Delete only known v2 children inside the
        // unique run prefix, without touching an adjacent production object.
        if(Transport.Provider is CloudProvider.S3 or CloudProvider.CloudflareR2)
        {
            foreach(var prefix in new[]{directory+"/versions",directory+"/devices",
                         directory+"/stats",directory})
                foreach(var item in await ListAsync(prefix,ct))
                {
                    if(!item.Path.StartsWith(prefix+"/",StringComparison.Ordinal) ||
                       item.Path[(prefix.Length+1)..].Contains('/'))
                        throw new SyncSafetyException("Test cleanup escaped its isolated prefix");
                    await DeleteAsync(item.Path,ct);
                }
            return;
        }
        if(Transport.Provider==CloudProvider.ProtonDrive)
            throw new NotSupportedException("KeePassXC.CloudTransport 1.0.2 cannot trash a Proton test folder; isolated cleanup requires a package API extension");
        // Google Drive and WebDAV delete an isolated directory recursively.
        await DeleteAsync(directory,ct);
    }
    public void Dispose()=>Transport.Dispose();
}

internal sealed class ConditionalCloudStoreAdapter(ICloudTransport transport,
    IConditionalCloudTransport conditional):CloudStoreAdapter(transport),IConditionalWriteStore
{
    public Task<string?> ReadTokenAsync(string path,CancellationToken ct)
        =>Guard(()=>conditional.ReadRevisionAsync(path,ct));
    public Task UploadConditionalAsync(string source,string path,string? token,CancellationToken ct)
        =>Guard(()=>conditional.UploadConditionalAsync(source,path,token,ct));
}
