using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace KeyPassXCCloudSync;

internal sealed record ProtonCliRelease(Version Version,Uri Download,string Sha512);
internal sealed record ProtonCliStatus(string State="idle",string Current="",string Latest="",string Path="",string Error="");

// The proton.me index is the only release and digest authority. Nothing is
// downloaded from GitHub mirrors or inferred from an untrusted version string.
internal static class ProtonCliUpdater
{
    internal static readonly Uri Index=new("https://proton.me/download/drive/cli/index.html");
    private static readonly SemaphoreSlim Operations=new(1,1);
    internal static ProtonCliStatus Status {get;private set;}=new();
    internal static event Action? Changed;

    internal static ProtonCliRelease ParseIndex(string html,Architecture architecture)
    {
        var arch=architecture switch
        {
            Architecture.X64=>"windows-x64",
            Architecture.Arm64=>"windows-arm64",
            _=>throw new PlatformNotSupportedException("Proton CLI has no supported Windows build for this CPU")
        };
        // Parse a *single* table row so a checksum for another architecture
        // can never be accidentally associated with the selected executable.
        var heading=Regex.Replace(html,@"<[^>]*>"," ");
        var match=Regex.Match(WebUtility.HtmlDecode(heading),
            @"Proton\s+Drive\s+CLI\s+(?<version>\d+\.\d+\.\d+)",RegexOptions.IgnoreCase);
        if(!match.Success || !Version.TryParse(match.Groups["version"].Value,out var version))
            throw new InvalidDataException("Proton release index has no valid version");
        foreach(Match row in Regex.Matches(html,@"<tr\b[^>]*>.*?</tr>",
            RegexOptions.IgnoreCase|RegexOptions.Singleline))
        {
            var rowText=WebUtility.HtmlDecode(Regex.Replace(row.Value,@"<[^>]*>"," "));
            if(!rowText.Contains("windows/"+ (architecture==Architecture.Arm64?"arm64":"x64"),
                StringComparison.OrdinalIgnoreCase))continue;
            var link=Regex.Match(row.Value,@"href\s*=\s*['""](?<href>[^'""]+)['""]",
                RegexOptions.IgnoreCase);
            var digest=Regex.Match(rowText,@"(?<![0-9a-f])(?<hash>[0-9a-f]{128})(?![0-9a-f])",
                RegexOptions.IgnoreCase);
            if(!link.Success || !digest.Success)break;
            var uri=new Uri(Index,WebUtility.HtmlDecode(link.Groups["href"].Value));
            var expected=$"/download/drive/cli/{version}/{arch}/proton-drive.exe";
            if(uri.Scheme!=Uri.UriSchemeHttps || uri.Host!="proton.me" || uri.Port!=443 ||
               uri.AbsolutePath!=expected || uri.Query.Length>0 || uri.Fragment.Length>0 ||
               uri.UserInfo.Length>0)
                throw new InvalidDataException("Unexpected Proton CLI download address");
            return new ProtonCliRelease(version,uri,digest.Groups["hash"].Value.ToUpperInvariant());
        }
        throw new InvalidDataException("Proton index has no matching Windows download and SHA-512");
    }

    private static HttpClient Client(TimeSpan timeout)=>new(){Timeout=timeout};

    internal static async Task<ProtonCliRelease> LatestAsync(CancellationToken ct)
    {
        using var client=Client(TimeSpan.FromSeconds(35));
        using var response=await client.GetAsync(Index,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength is > 1_000_000)
            throw new InvalidDataException("Proton release index exceeds expected size");
        var html=await response.Content.ReadAsStringAsync(ct);
        if(html.Length>1_000_000)throw new InvalidDataException("Proton release index is too large");
        return ParseIndex(html,RuntimeInformation.ProcessArchitecture);
    }

    internal static async Task<Version?> ReadVersionAsync(string path,CancellationToken ct)
    {
        if(!File.Exists(path))return null;
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo(path)
        {
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        process.StartInfo.ArgumentList.Add("version");
        process.StartInfo.Environment["PROTON_DRIVE_LOG_LEVEL"]="ERROR";
        if(!process.Start())throw new IOException("Proton CLI could not be started");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var output=process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors=process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var versionText=(await output)+" "+(await errors);
            if(process.ExitCode!=0)throw new InvalidDataException("Proton CLI version command failed");
            var found=Regex.Match(versionText,@"(?<!\d)(\d+\.\d+\.\d+)(?!\d)");
            if(!found.Success || !Version.TryParse(found.Groups[1].Value,out var version))
                throw new InvalidDataException("Proton CLI did not report its version");
            return version;
        }
        catch
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            throw;
        }
    }

    private static IEnumerable<string> CandidatePaths(string? configPath)
    {
        var paths=new List<string>();
        // Explicit per-cloud paths take precedence: the automatic locator may
        // point to a different executable (or a different Proton account).
        if(!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            try
            {
                var cfg=AppOptions.Load(configPath);
                foreach(var cloud in cfg.Clouds.Where(c=>c.Provider=="ProtonDrive"))
                {
                    var path=PlatformServices.Current.Paths.ResolveProtonExecutable(
                        cloud.Executable,configPath);
                    if(path.Length>0 && File.Exists(path))paths.Add(path);
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or
                ArgumentException or FormatException or SyncSafetyException)
            { /* A broken INI cannot prevent an automatic local CLI check. */ }
        }
        var automatic=PlatformServices.Current.Paths.ResolveProtonExecutable("",configPath);
        if(automatic.Length>0 && File.Exists(automatic))paths.Add(automatic);
        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal static async Task CheckAsync(string? configPath,CancellationToken ct=default)
    {
        if(!await Operations.WaitAsync(0,ct))return;
        try
        {
            var paths=CandidatePaths(configPath).ToArray();
            if(paths.Length==0)
            {
                Status=new("missing");return;
            }
            Status=new("checking",Path:paths[0]);
            Changed?.Invoke();
            var remote=await LatestAsync(ct);
            ProtonCliStatus? firstCurrent=null;
            foreach(var path in paths)
            {
                var local=await ReadVersionAsync(path,ct);
                var state=new ProtonCliStatus(remote.Version>local?"available":"current",
                    local?.ToString()??"",remote.Version.ToString(),path);
                if(state.State=="available") {Status=state;return;}
                firstCurrent??=state;
            }
            Status=firstCurrent??new("missing");
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception ex){Status=new("error",Path:Status.Path,Error:ex.Message);}
        finally{Operations.Release();Changed?.Invoke();}
    }

    // Download into the target directory, hash the *complete* bytes, then
    // replace under an application-wide idle lease with a rollback backup.
    internal static async Task<string> InstallAsync(string? configPath,bool forceLocal,
        Func<bool> protonIdle,IProgress<UpdateOperationProgress>? progress,CancellationToken ct)
    {
        await Operations.WaitAsync(ct);
        string? temporary=null;
        try
        {
            progress?.Report(new("checking-proton"));
            var release=await LatestAsync(ct);
            var configured=PlatformServices.Current.Paths.ResolveProtonExecutable("",configPath);
            var pending=Status.State=="available" && File.Exists(Status.Path)?Status.Path:configured;
            var target=forceLocal || pending.Length==0?ProtonCliLocator.ManagedPath:pending;
            var current=await ReadVersionAsync(target,ct);
            if(current is not null && current>=release.Version)
                return target;
            var directory=Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            temporary=Path.Combine(directory,".proton-drive-"+Guid.NewGuid().ToString("N")+".exe");
            using(var http=Client(TimeSpan.FromMinutes(12)))
            using(var response=await http.GetAsync(release.Download,HttpCompletionOption.ResponseHeadersRead,ct))
            {
                response.EnsureSuccessStatusCode();
                const long maximum=450L*1024*1024;
                if(response.Content.Headers.ContentLength is >maximum)
                    throw new InvalidDataException("Proton CLI download is too large");
                await using var input=await response.Content.ReadAsStreamAsync(ct);
                await using var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,
                    FileShare.None,81920,FileOptions.Asynchronous);
                var buffer=new byte[131072];long received=0;
                while(true)
                {
                    var size=await input.ReadAsync(buffer,ct);
                    if(size==0)break;
                    received+=size;
                    if(received>maximum)throw new InvalidDataException("Proton CLI download is too large");
                    await file.WriteAsync(buffer.AsMemory(0,size),ct);
                    progress?.Report(new("downloading-proton",
                        response.Content.Headers.ContentLength is long count && count>0
                            ?(int)Math.Clamp(received*100/count,0,100):null));
                }
                await file.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report(new("verifying-proton"));
            await using(var input=File.OpenRead(temporary))
            {
                var actual=Convert.ToHexString(await SHA512.HashDataAsync(input,ct));
                if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual),
                   Convert.FromHexString(release.Sha512)))
                    throw new InvalidDataException("Proton CLI SHA-512 does not match proton.me");
            }
            // A binary must pass its own version command before it is installed.
            if(await ReadVersionAsync(temporary,ct)!=release.Version)
                throw new InvalidDataException("Downloaded Proton CLI reports an unexpected version");
            progress?.Report(new("waiting-proton"));
            IDisposable? lease=null;
            var started=DateTimeOffset.UtcNow;
            while(lease is null)
            {
                ct.ThrowIfCancellationRequested();
                if(protonIdle())
                {
                    lease=ProtonCliMaintenance.TryBeginUpdate();
                    if(lease is not null && !protonIdle())
                    {lease.Dispose();lease=null;}
                }
                if(lease is not null)break;
                if(DateTimeOffset.UtcNow-started>TimeSpan.FromMinutes(3))
                    throw new TimeoutException("Proton synchronization is still running; retry after it finishes");
                await Task.Delay(250,ct);
            }
            using(lease)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new("installing-proton"));
                var backup=target+".previous."+Guid.NewGuid().ToString("N");
                if(File.Exists(target))
                {
                    // Do not touch the original if Windows cannot replace an
                    // executable currently opened by an external process.
                    File.Replace(temporary,target,backup);
                    temporary=null;
                    try
                    {
                        if(await ReadVersionAsync(target,CancellationToken.None)!=release.Version)
                            throw new InvalidDataException("Installed Proton CLI version mismatch");
                    }
                    catch
                    {
                        File.Replace(backup,target,null);
                        throw;
                    }
                    // Failure to clean a validated backup is not an update failure.
                    try { File.Delete(backup); }
                    catch(IOException) { }
                    catch(UnauthorizedAccessException) { }
                }
                else
                {
                    File.Move(temporary,target);
                    temporary=null;
                    try
                    {
                        if(await ReadVersionAsync(target,CancellationToken.None)!=release.Version)
                            throw new InvalidDataException("Installed Proton CLI version mismatch");
                    }
                    catch{File.Delete(target);throw;}
                }
            }
            Status=new("current",release.Version.ToString(),release.Version.ToString(),target);
            Changed?.Invoke();
            return target;
        }
        finally
        {
            if(temporary is not null)try{File.Delete(temporary);}catch(IOException){}
            Operations.Release();
        }
    }
}
