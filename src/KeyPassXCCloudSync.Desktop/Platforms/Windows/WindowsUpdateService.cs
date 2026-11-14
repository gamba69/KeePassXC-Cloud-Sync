using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sigstore;
using Tuf;

namespace KeyPassXCCloudSync;

// Update execution is confined to the Windows adapter. No update code can edit
// sync state, databases, cloud endpoints or external Proton tools.
internal sealed class WindowsUpdateService : IUpdateService
{
    private const string Repository = ReleaseUpdatePolicy.Repository;
    private const string Releases = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string Api = "https://api.github.com/repos/" + Repository + "/";
    // GitHub CLI's TUF bootstrap root, pinned to an exact commit and SHA-256.
    // Root rotations require a deliberate application update, never TOFU.
    private const string TufBootstrap = "https://raw.githubusercontent.com/cli/cli/3d5a9e355a837f2f5ee78d7484c118cc1342ad1a/pkg/cmd/attestation/verification/embed/tuf-repo.github.com/root.json";
    private const string TufBootstrapSha256 = "98cba97be9075bc98b2322de3de85fbd1b70ec7392991dfd2f53d215bede1a8d";
    private const long MaxResponse = 3 * 1024 * 1024;
    private const long MaxZip = 256L * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1,1);
    private readonly string _root;
    private readonly string _settingsPath;
    private UpdateSnapshot _snapshot = new();
    private string? _readyDirectory;
    private bool _disposed;

    internal WindowsUpdateService(HttpMessageHandler? handler = null, string? root = null)
    {
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeyPassXCCloudSync", "updates");
        _settingsPath = Path.Combine(_root,"preferences.txt");
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromMinutes(3);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("KeyPassXCCloudSync-Updater/7.2");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        Preferences = ReadPreferences();
        RestorePending();
    }

    public UpdatePreferences Preferences { get; private set; }
    public UpdateSnapshot Snapshot => _snapshot;
    public bool IsReady => _readyDirectory is not null;
    public event Action? Changed;
    private void Publish(UpdateSnapshot snapshot) { _snapshot = snapshot; Changed?.Invoke(); }

    private UpdatePreferences ReadPreferences()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new();
            var lines = File.ReadAllLines(_settingsPath);
            if (lines.Length != 3) return new();
            return Validate(new UpdatePreferences(lines[0] == "true", lines[1] == "true", lines[2]));
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    private static UpdatePreferences Validate(UpdatePreferences value) =>
        value with { Installation = value.Installation is "exit" or "ask" or "manual"
            ? value.Installation : "exit" };

    public void SavePreferences(UpdatePreferences value)
    {
        value = Validate(value);
        Directory.CreateDirectory(_root);
        var tmp = Path.Combine(_root, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(tmp, (value.CheckAutomatically ? "true" : "false") + "\n" +
                (value.DownloadAutomatically ? "true" : "false") + "\n" + value.Installation + "\n",
                new UTF8Encoding(false));
            File.Move(tmp, _settingsPath, true);
            Preferences = value;
            Changed?.Invoke();
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private void RestorePending()
    {
        try
        {
            var pointer = Path.Combine(_root,"ready.txt");
            if (!File.Exists(pointer)) return;
            var tag = File.ReadAllText(pointer).Trim();
            var directory = Path.Combine(_root,"pending-"+ReleaseUpdatePolicy.ParseVersion(tag).ToString(3));
            if (!ReleaseUpdatePolicy.IsNewer(tag,BuildInfo.Version)) return;
            VerifyPayloadManifest(directory);
            _readyDirectory = directory;
            Publish(new UpdateSnapshot("ready",tag,
                File.Exists(Path.Combine(directory,"unverified.txt"))?"mutable":"",
                File.Exists(Path.Combine(directory,"unverified.txt"))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            // An interrupted download is never treated as a verified update.
        }
    }

    public async Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (!manual && !Preferences.CheckAutomatically) return;
        if (!await _gate.WaitAsync(0,cancellationToken)) return;
        try
        {
            var cachedTag=_readyDirectory is not null ? _snapshot.Version : null;
            Publish(new UpdateSnapshot("checking"));
            using var release = await GetJsonAsync(Releases,cancellationToken);
            var json = release.RootElement;
            if (json.GetProperty("draft").GetBoolean() || json.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("GitHub did not return a stable release.");
            var tag = json.GetProperty("tag_name").GetString() ?? "";
            if (!ReleaseUpdatePolicy.IsNewer(tag, BuildInfo.Version))
            {
                if (_readyDirectory is null) Publish(new UpdateSnapshot("current"));
                return;
            }
            bool immutable = json.GetProperty("immutable").ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException("Missing GitHub immutability status.")
            };
            var zipName=ReleaseUpdatePolicy.ArchiveName(tag);
            var shaName=ReleaseUpdatePolicy.ChecksumName(tag);
            JsonElement zip = default, sha = default;
            foreach (var asset in json.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == zipName) zip=asset;
                if (asset.GetProperty("name").GetString() == shaName) sha=asset;
            }
            if (zip.ValueKind==JsonValueKind.Undefined || sha.ValueKind==JsonValueKind.Undefined)
                throw new InvalidDataException("Missing release ZIP or checksum.");
            var zipUrl=ReleaseAssetUrl(zip,tag,zipName);
            var shaUrl=ReleaseAssetUrl(sha,tag,shaName);
            var warning=immutable?"":"GitHub Release Immutability is disabled; authenticity is not cryptographically verified.";
            if (_readyDirectory is not null && cachedTag==tag)
            {
                if (!immutable || !File.Exists(Path.Combine(_readyDirectory,"unverified.txt")))
                {
                    Publish(new UpdateSnapshot("ready",tag,warning,!immutable));
                    return;
                }
                // A cached mutable download must never be promoted to an
                // authenticated update merely because API metadata changed.
                _readyDirectory=null;
            }
            Publish(new UpdateSnapshot("available",tag,warning,!immutable));
            if (!Preferences.DownloadAutomatically && !manual) return;
            var directory=Path.Combine(_root,"pending-"+ReleaseUpdatePolicy.ParseVersion(tag).ToString(3));
            if (Directory.Exists(directory)) Directory.Delete(directory,true);
            Directory.CreateDirectory(directory);
            try
            {
                var zipPath=Path.Combine(directory,zipName);
                await DownloadAsync(zipUrl,zipPath,MaxZip,cancellationToken);
                var checksum=await GetTextAsync(shaUrl,4096,cancellationToken);
                var expectedDigest=zip.TryGetProperty("digest",out var digest)
                    ?digest.GetString():null;
                ReleaseUpdatePolicy.VerifyChecksum(zipPath,checksum,zipName,expectedDigest);
                if (immutable)
                {
                    try
                    {
                        await VerifyImmutableReleaseAsync(tag,zipName,
                            ReleaseUpdatePolicy.HashFile(zipPath),cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new InvalidDataException(
                            "GitHub release attestation verification failed: " + ex.Message,ex);
                    }
                }
                ReleaseUpdatePolicy.ExtractVerifiedArchive(zipPath,Path.Combine(directory,"payload"));
                WritePayloadManifest(directory);
                if (!immutable) File.WriteAllText(Path.Combine(directory,"unverified.txt"),"mutable");
                File.WriteAllText(Path.Combine(directory,"verified.txt"),tag);
                Directory.CreateDirectory(_root);
                File.WriteAllText(Path.Combine(_root,"ready.txt"),tag);
                _readyDirectory=directory;
                Publish(new UpdateSnapshot("ready",tag,warning,!immutable));
            }
            catch
            {
                if (Directory.Exists(directory)) Directory.Delete(directory,true);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Publish(new UpdateSnapshot("error", "", ex.Message));
        }
        finally { _gate.Release(); }
    }

    private static Uri ReleaseAssetUrl(JsonElement asset,string tag,string expectedName)
    {
        var raw=asset.GetProperty("browser_download_url").GetString()??"";
        if (!Uri.TryCreate(raw,UriKind.Absolute,out var uri) || uri.Scheme!="https" ||
            uri.Host!="github.com" ||
            uri.AbsolutePath!=$"/{Repository}/releases/download/{tag}/{expectedName}")
            throw new InvalidDataException("Unexpected release asset location.");
        return uri;
    }

    private async Task<JsonDocument> GetJsonAsync(string url,CancellationToken ct)
    {
        var data=await GetBytesAsync(new Uri(url),MaxResponse,ct);
        return JsonDocument.Parse(data);
    }
    private async Task<string> GetTextAsync(Uri url,long limit,CancellationToken ct)=>
        Encoding.UTF8.GetString(await GetBytesAsync(url,limit,ct));
    private async Task<byte[]> GetBytesAsync(Uri url,long limit,CancellationToken ct)
    {
        if (url.Scheme!=Uri.UriSchemeHttps) throw new InvalidDataException("HTTPS required.");
        using var response=await _client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } known && known>limit)
            throw new InvalidDataException("Remote data exceeds size limit.");
        await using var input=await response.Content.ReadAsStreamAsync(ct);
        using var output=new MemoryStream();
        var buffer=new byte[64*1024];
        int read;
        while ((read=await input.ReadAsync(buffer,ct))!=0)
        {
            if (output.Length+read>limit) throw new InvalidDataException("Remote data exceeds size limit.");
            output.Write(buffer,0,read);
        }
        return output.ToArray();
    }
    private async Task DownloadAsync(Uri url,string destination,long limit,CancellationToken ct)
    {
        using var response=await _client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length>limit)
            throw new InvalidDataException("Archive exceeds size limit.");
        await using var input=await response.Content.ReadAsStreamAsync(ct);
        await using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        var buffer=new byte[64*1024];
        int read;
        while ((read=await input.ReadAsync(buffer,ct))!=0)
        {
            if (output.Length+read>limit) throw new InvalidDataException("Archive exceeds size limit.");
            await output.WriteAsync(buffer.AsMemory(0,read),ct);
        }
    }

    // Release signatures are issued by GitHub's release-specific Fulcio identity.
    // Trusted keys come from GitHub's TUF repository with an independently pinned
    // bootstrap root. This validates the signature, certificate and signed time.
    private async Task VerifyImmutableReleaseAsync(string tag,string zipName,string zipDigest,CancellationToken ct)
    {
        var refUrl=Api+"git/ref/tags/"+Uri.EscapeDataString(tag);
        using var refJson=await GetJsonAsync(refUrl,ct);
        var gitSha=refJson.RootElement.GetProperty("object").GetProperty("sha").GetString()??"";
        if (gitSha.Length!=40 || !gitSha.All(Uri.IsHexDigit))
            throw new InvalidDataException("Release tag ref is invalid.");
        var url=Api+"attestations/sha1:"+gitSha+"?predicate_type=release&per_page=100";
        using var attestations=await GetJsonAsync(url,ct);
        var entries=attestations.RootElement.GetProperty("attestations");
        if (entries.ValueKind!=JsonValueKind.Array || entries.GetArrayLength()==0)
            throw new InvalidDataException("Immutable release has no attestations.");
        var bootstrap=await GetBytesAsync(new Uri(TufBootstrap),MaxResponse,ct);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bootstrap)),TufBootstrapSha256,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub TUF bootstrap does not match its pinned digest.");
        // GitHub's signed trusted_root.json uses bare DNS names for its Fulcio
        // and TSA authority URI fields. Sigstore 1.1.1 rejects them as relative
        // URIs. Fetch and authenticate the target with the same TUF client that
        // its trust-root provider uses, then adapt only those known names.
        var tufUrl=new Uri("https://tuf-repo.github.com/");
        using var tuf=new TufClient(new TufClientOptions
        {
            MetadataBaseUrl=tufUrl,
            TargetsBaseUrl=new Uri(tufUrl,"targets/"),
            TrustedRoot=bootstrap
        });
        var target=await tuf.GetTargetAsync("trusted_root.json",ct);
        if (target.Metadata.Expires<=DateTimeOffset.UtcNow)
            throw new InvalidDataException("GitHub TUF trusted-root metadata is expired.");
        var trust=new InMemoryTrustRootProvider(
            GithubTrustRootCompatibility.ParseVerifiedTarget(target.Content));
        var verifier=new SigstoreVerifier(trust);
        var policy=new VerificationPolicy
        {
            CertificateIdentity=new CertificateIdentity
            {
                SubjectAlternativeName="https://dotcom.releases.github.com"
            },
            RequireTransparencyLog=false,
            RequireSignedTimestamps=true,
            RequireSignedCertificateTimestamps=false
        };
        var matches=0;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("initiator",out var initiator) || initiator.GetString()!="github")
                continue;
            byte[] bundleBytes;
            if (entry.TryGetProperty("bundle_url",out var bundleUrl) &&
                bundleUrl.ValueKind==JsonValueKind.String && bundleUrl.GetString() is {Length:>0} bundleAddress)
            {
                if (!Uri.TryCreate(bundleAddress,UriKind.Absolute,out var blobUrl) ||
                    blobUrl.Scheme!=Uri.UriSchemeHttps)
                    throw new InvalidDataException("Invalid GitHub attestation URL.");
                var compressed=await GetBytesAsync(blobUrl,MaxResponse,ct);
                bundleBytes=RawSnappy.Decode(compressed,4*1024*1024);
            }
            else if (entry.TryGetProperty("bundle",out var embedded))
                bundleBytes=Encoding.UTF8.GetBytes(embedded.GetRawText());
            else throw new InvalidDataException("Missing GitHub attestation bundle.");
            using var parsed=JsonDocument.Parse(bundleBytes);
            if (!parsed.RootElement.TryGetProperty("dsseEnvelope",out var envelope))
                continue;
            var payload=envelope.GetProperty("payload").GetString()??"";
            var statement=Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            // Never trust the statement before verification. Verification binds
            // the DSSE payload to GitHub's Fulcio issuer and authenticated TSA.
            using var statementDoc=JsonDocument.Parse(statement);
            if (!statementDoc.RootElement.TryGetProperty("predicate",out var predicate) ||
                !predicate.TryGetProperty("tag",out var signedTag) || signedTag.GetString()!=tag)
                continue;
            var bundle=SigstoreBundle.Deserialize(Encoding.UTF8.GetString(bundleBytes));
            await verifier.VerifyAsync(ReadOnlyMemory<byte>.Empty,bundle,policy,ct);
            ReleaseUpdatePolicy.VerifySignedStatement(statement,tag,gitSha,zipName,zipDigest);
            matches++;
        }
        if (matches!=1) throw new InvalidDataException("Expected exactly one verified GitHub release attestation.");
    }


    internal static void WritePayloadManifest(string stage)
    {
        var files=Directory.GetFiles(Path.Combine(stage,"payload"),"*",SearchOption.TopDirectoryOnly);
        if (files.Length==0 || !files.Any(f=>Path.GetFileName(f).Equals(
            ReleaseUpdatePolicy.Executable,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Incomplete extracted update.");
        File.WriteAllLines(Path.Combine(stage,"files.txt"),files.OrderBy(Path.GetFileName,StringComparer.Ordinal)
            .Select(f=>Path.GetFileName(f)+"\t"+ReleaseUpdatePolicy.HashFile(f)),
            new UTF8Encoding(false));
    }

    internal static void VerifyPayloadManifest(string stage)
    {
        if (!File.Exists(Path.Combine(stage,"verified.txt")) ||
            !File.Exists(Path.Combine(stage,"files.txt")))
            throw new InvalidDataException("Missing verified update manifest.");
        var lines=File.ReadAllLines(Path.Combine(stage,"files.txt"));
        var actual=Directory.GetFiles(Path.Combine(stage,"payload"),"*",SearchOption.TopDirectoryOnly);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var pair=line.Split('\t');
            if (pair.Length!=2 || !ReleaseUpdatePolicy.IsOwnedFile(pair[0]) ||
                !seen.Add(pair[0]) || pair[1].Length!=64 || !pair[1].All(Uri.IsHexDigit))
                throw new InvalidDataException("Unsafe update manifest entry.");
            var file=Path.Combine(stage,"payload",pair[0]);
            if (!File.Exists(file) || !string.Equals(ReleaseUpdatePolicy.HashFile(file),pair[1],
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged update file has changed.");
        }
        if (!seen.Contains(ReleaseUpdatePolicy.Executable) || actual.Length!=seen.Count ||
            actual.Any(x=>!seen.Contains(Path.GetFileName(x))))
            throw new InvalidDataException("Unexpected update payload files.");
    }

    public void ValidateInstallation()
    {
        var stage=_readyDirectory??throw new InvalidOperationException("No prepared update is available.");
        VerifyPayloadManifest(stage);
        var current=Environment.ProcessPath??throw new IOException("Application path is unavailable.");
        var directory=Path.GetDirectoryName(current)!;
        var installation=new DirectoryInfo(directory);
        if (installation.LinkTarget is not null ||
            (installation.Attributes & FileAttributes.ReparsePoint)!=0)
            throw new IOException("Updating through a reparse-point directory is not supported.");
        var bytes=Directory.GetFiles(Path.Combine(stage,"payload"))
            .Sum(file=>new FileInfo(file).Length);
        var drive=new DriveInfo(Path.GetPathRoot(directory)!);
        if (drive.AvailableFreeSpace<Math.Max(bytes*2,50L*1024*1024))
            throw new IOException("Insufficient free space for the update and rollback.");
        var probe=Path.Combine(directory,".kps-update-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using (File.Create(probe)) { }
        }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    public void LaunchInstaller(bool restart,string configPath,bool background)
    {
        ValidateInstallation();
        var stage=_readyDirectory??throw new InvalidOperationException("No verified update prepared.");
        if (File.ReadAllText(Path.Combine(stage,"verified.txt")).Trim()!=Snapshot.Version)
            throw new InvalidDataException("Pending update marker mismatch.");
        VerifyPayloadManifest(stage);
        var current=Environment.ProcessPath??throw new InvalidOperationException("Missing application path.");
        if (!string.Equals(Path.GetFileName(current),ReleaseUpdatePolicy.Executable,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected application executable.");
        var helper=Path.Combine(stage,"update-helper.exe");
        File.Copy(current,helper,true);
        // A NativeAOT binary can depend on private native runtime DLLs.
        // Copy them beside the temporary EXE so it remains independently runnable.
        foreach(var dll in Directory.GetFiles(Path.GetDirectoryName(current)!,"*.dll"))
            if(ReleaseUpdatePolicy.IsOwnedFile(Path.GetFileName(dll)))
                File.Copy(dll,Path.Combine(stage,Path.GetFileName(dll)),true);
        using var self=Process.GetCurrentProcess();
        var info=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true};
        foreach (var argument in new[] {"--apply-update",Path.GetDirectoryName(current)!,stage,
            self.Id.ToString(CultureInfo.InvariantCulture),self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            restart?"1":"0",configPath,background?"1":"0"}) info.ArgumentList.Add(argument);
        if (Process.Start(info) is null) throw new IOException("Could not launch update helper.");
    }

    // Recovery is performed by a TEMPORARY process. Never overwrite the
    // executable while it is still running in the startup process.
    internal static bool LaunchInterruptedRecovery(string[] startupArgs)
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeyPassXCCloudSync","updates");
        var journal=Path.Combine(root,"installing.txt");
        if (!File.Exists(journal)) return false;
        var stage=Path.GetFullPath(File.ReadAllText(journal).Trim());
        if (!stage.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid recovery directory.");
        var target=Path.GetFullPath(File.ReadAllText(Path.Combine(stage,"target.txt")).Trim());
        var current=Environment.ProcessPath??throw new IOException("Missing recovery EXE.");
        if (!string.Equals(target,Path.GetDirectoryName(current),StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recovery target does not match running application.");
        // Use the known-good old EXE cached before the update, never the
        // possibly damaged new executable being recovered.
        var helper=Path.Combine(stage,"update-helper.exe");
        if(!File.Exists(helper))
            throw new IOException("The temporary recovery helper is missing.");
        using var self=Process.GetCurrentProcess();
        var info=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true};
        foreach (var value in new[]{"--recover-update",stage,target,
            self.Id.ToString(CultureInfo.InvariantCulture),
            self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)})
            info.ArgumentList.Add(value);
        foreach (var argument in startupArgs) info.ArgumentList.Add(argument);
        if (Process.Start(info) is null) throw new IOException("Could not start recovery helper.");
        return true;
    }

    internal static int ApplyRecovery(string[] args)
    {
        if (args.Length<5 || args[0]!="--recover-update") return 2;
        try
        {
            var stage=Path.GetFullPath(args[1]);
            var target=Path.GetFullPath(args[2]);
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KeyPassXCCloudSync","updates");
            if (!stage.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(File.ReadAllText(Path.Combine(stage,"target.txt")).Trim(),target,
                    StringComparison.OrdinalIgnoreCase)) return 3;
            if (!int.TryParse(args[3],NumberStyles.None,CultureInfo.InvariantCulture,out var pid) ||
                !long.TryParse(args[4],NumberStyles.None,CultureInfo.InvariantCulture,out var ticks)) return 4;
            try
            {
                using var old=Process.GetProcessById(pid);
                if (old.StartTime.ToUniversalTime().Ticks!=ticks || !old.WaitForExit(120000)) return 5;
            }
            catch (ArgumentException) { /* Original process has exited. */ }
            RestoreBackup(stage,target,root);
            File.Delete(Path.Combine(root,"installing.txt"));
            File.WriteAllText(Path.Combine(root,"completed.txt"),stage);
            var info=new ProcessStartInfo(Path.Combine(target,ReleaseUpdatePolicy.Executable))
                {UseShellExecute=false};
            for (var i=5;i<args.Length;i++) info.ArgumentList.Add(args[i]);
            if (Process.Start(info) is null) return 6;
            return 0;
        }
        catch (Exception) { return 1; }
    }

    internal static int ApplyUpdate(string[] args)
    {
        if (args.Length!=8 || args[0]!="--apply-update" || args[5] is not ("0" or "1") ||
            args[7] is not ("0" or "1")) return 2;
        try
        {
            var target=Path.GetFullPath(args[1]);
            var stage=Path.GetFullPath(args[2]);
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KeyPassXCCloudSync","updates");
            if (!stage.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(stage,"verified.txt")) ||
                !File.Exists(Path.Combine(target,ReleaseUpdatePolicy.Executable))) return 3;
            if (!int.TryParse(args[3],NumberStyles.None,CultureInfo.InvariantCulture,out var pid) ||
                !long.TryParse(args[4],NumberStyles.None,CultureInfo.InvariantCulture,out var ticks)) return 4;
            try
            {
                using var original=Process.GetProcessById(pid);
                if (original.StartTime.ToUniversalTime().Ticks!=ticks || !original.WaitForExit(120000)) return 5;
            }
            catch (ArgumentException) { /* Old process exited. */ }
            ApplyVerifiedFiles(stage,target,root,
                validateNew:()=>SmokeTestExecutable(Path.Combine(target,ReleaseUpdatePolicy.Executable)));
            if (args[5]=="1")
            {
                var start=new ProcessStartInfo(Path.Combine(target,ReleaseUpdatePolicy.Executable))
                    {UseShellExecute=false};
                start.ArgumentList.Add(args[7]=="1"?"--background":"--show");
                start.ArgumentList.Add("--config");start.ArgumentList.Add(args[6]);
                Process.Start(start);
            }
            return 0;
        }
        catch (Exception) { return 1; }
    }

    // Only files installed by an earlier verified update are eligible for removal.
    // Use a per-installation manifest: two portable copies under one Windows
    // profile must never inherit each other's file ownership.
    internal static string InstalledFilesManifestPath(string root,string target)
    {
        var normalized=Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(Path.GetFullPath(root),"owned",identity+".txt");
    }

    private static Dictionary<string,string> ReadInstalledFiles(string manifest)
    {
        var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var parent=Path.GetDirectoryName(manifest)!;
        if (Directory.Exists(parent) &&
            (File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Installed-file directory is a reparse point.");
        if (!File.Exists(manifest)) return files;
        if ((File.GetAttributes(manifest)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Installed-file manifest is a reparse point.");
        foreach (var line in File.ReadAllLines(manifest))
        {
            var pair=line.Split('\t');
            if (pair.Length!=2 || !ReleaseUpdatePolicy.IsOwnedFile(pair[0]) ||
                pair[1].Length!=64 || !pair[1].All(Uri.IsHexDigit) ||
                !files.TryAdd(pair[0],pair[1]))
                throw new InvalidDataException("Installed-file manifest contains an unsafe entry.");
        }
        if (!files.ContainsKey(ReleaseUpdatePolicy.Executable))
            throw new InvalidDataException("Installed-file manifest lacks the application executable.");
        return files;
    }

    private static void WriteInstalledFiles(string source,string destination)
    {
        var directory=Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("Installed-file manifest directory is a reparse point.");
        var temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.Copy(source,temporary);File.Move(temporary,destination,true); }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsUnmodifiedOwnedFile(string target,string name,string expectedHash)
    {
        var path=Path.Combine(target,name);
        return File.Exists(path) &&
            (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0 &&
            string.Equals(ReleaseUpdatePolicy.HashFile(path),expectedHash,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void RestoreInstalledFiles(string stage,string root,string target)
    {
        var manifest=InstalledFilesManifestPath(root,target);
        var previous=Path.Combine(stage,"owned-before.txt");
        if (File.Exists(previous)) WriteInstalledFiles(previous,manifest);
        else if (File.Exists(manifest)) File.Delete(manifest);
    }

    // A testable filesystem transaction: the helper calls this only after the
    // owning application has exited and released every KDBX lock.
    internal static void ApplyVerifiedFiles(string stage,string target,string root,
        Action<string>? beforeReplace=null,Func<bool>? validateNew=null,
        Action<string>? beforeDelete=null)
    {
        VerifyPayloadManifest(stage);
        stage=Path.GetFullPath(stage);
        target=Path.GetFullPath(target);
        root=Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(target,ReleaseUpdatePolicy.Executable)))
            throw new InvalidDataException("Application target executable is missing.");
        if (File.Exists(Path.Combine(root,"installing.txt")))
            throw new IOException("A previous installation attempt requires recovery.");
        var files=Directory.GetFiles(Path.Combine(stage,"payload"),"*",SearchOption.TopDirectoryOnly);
        var currentNames=files.Select(file=>Path.GetFileName(file)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownership=InstalledFilesManifestPath(root,target);
        var previouslyInstalled=ReadInstalledFiles(ownership);
        // A previously managed DLL that was edited after installation is not
        // ours to delete. Without a prior manifest, never guess ownership.
        var obsolete=previouslyInstalled
            .Where(item=>!currentNames.Contains(item.Key) &&
                IsUnmodifiedOwnedFile(target,item.Key,item.Value))
            .Select(item=>item.Key).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
        var backup=Path.Combine(stage,"backup");
        if (Directory.Exists(backup))
        {
            if (File.Exists(Path.Combine(root,"installing.txt")))
                throw new IOException("A previous installation attempt requires recovery.");
            // Until the journal exists, target files have not been changed.
            // Remove backups from interrupted preflight preparations only.
            Directory.Delete(backup,true);
        }
        Directory.CreateDirectory(backup);
        var previousOwnership=Path.Combine(stage,"owned-before.txt");
        if (File.Exists(previousOwnership)) File.Delete(previousOwnership);
        if (File.Exists(ownership)) File.Copy(ownership,previousOwnership);
        foreach (var name in files.Select(file=>Path.GetFileName(file)!)
            .Concat(obsolete).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var old=Path.Combine(target,name);
            if (File.Exists(old)) File.Copy(old,Path.Combine(backup,name),true);
        }
        File.WriteAllLines(Path.Combine(stage,"original-files.txt"),
            Directory.GetFiles(backup).Select(f=>Path.GetFileName(f)!));
        File.WriteAllText(Path.Combine(stage,"target.txt"),target);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"installing.txt"),stage);
        try
        {
            foreach (var file in files)
            {
                var name=Path.GetFileName(file);
                beforeReplace?.Invoke(name);
                var dst=Path.Combine(target,name);
                var temporary=Path.Combine(target,".kps-update-"+Guid.NewGuid().ToString("N")+".tmp");
                try { File.Copy(file,temporary);File.Move(temporary,dst,true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            foreach (var name in obsolete)
            {
                beforeDelete?.Invoke(name);
                // The file might have been modified between planning and
                // removal. Re-check its hash and never follow reparse points.
                if (IsUnmodifiedOwnedFile(target,name,previouslyInstalled[name]))
                    File.Delete(Path.Combine(target,name));
            }
            if(validateNew is not null && !validateNew())
                throw new IOException("Updated EXE failed its startup smoke check.");
            WriteInstalledFiles(Path.Combine(stage,"files.txt"),ownership);
            var previous=Path.Combine(root,"previous");
            if (Directory.Exists(previous)) Directory.Delete(previous,true);
            Directory.Move(backup,previous);
            File.WriteAllText(Path.Combine(root,"completed.txt"),stage);
            File.Delete(Path.Combine(root,"installing.txt"));
        }
        catch
        {
            // Do not lose the journal if rollback itself fails: the recovery
            // helper retries automatically before the next normal startup.
            RestoreBackup(stage,target,root);
            File.Delete(Path.Combine(root,"installing.txt"));
            throw;
        }
    }

    private static bool SmokeTestExecutable(string exe)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true};
        info.ArgumentList.Add("--selftest-update");
        using var process=Process.Start(info);
        if(process is null)return false;
        if(!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree:true);
            process.WaitForExit(5000);
            return false;
        }
        return process.ExitCode==0;
    }

    internal static void RestoreBackup(string stage,string target,string root)
    {
        var backup=Path.Combine(stage,"backup");
        if (!Directory.Exists(backup))
            backup=Path.Combine(root,"previous");
        if (!Directory.Exists(backup)) throw new IOException("Rollback backup is missing.");
        var originals=File.ReadAllLines(Path.Combine(stage,"original-files.txt"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in Directory.GetFiles(Path.Combine(stage,"payload"),"*",SearchOption.TopDirectoryOnly))
        {
            var name=Path.GetFileName(candidate);
            if (!ReleaseUpdatePolicy.IsOwnedFile(name)) throw new IOException("Unsafe rollback entry.");
            if (!originals.Contains(name)) File.Delete(Path.Combine(target,name));
        }
        foreach (var file in Directory.GetFiles(backup,"*",SearchOption.TopDirectoryOnly))
        {
            var name=Path.GetFileName(file);
            if (!ReleaseUpdatePolicy.IsOwnedFile(name)) throw new IOException("Unsafe recovery entry.");
            File.Copy(file,Path.Combine(target,name),true);
        }
        RestoreInstalledFiles(stage,root,target);
    }

    public void CleanupCompleted()
    {
        try
        {
            var marker=Path.Combine(_root,"completed.txt");
            if (!File.Exists(marker)) return;
            var stage=File.ReadAllText(marker).Trim();
            if (Path.GetFullPath(stage).StartsWith(Path.GetFullPath(_root)+Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(stage)) Directory.Delete(stage,true);
                File.Delete(marker);
                var ready=Path.Combine(_root,"ready.txt");
                if (File.Exists(ready)) File.Delete(ready);
                if (!File.Exists(Path.Combine(_root,"installing.txt")))
                    foreach (var unused in Directory.GetDirectories(_root,"pending-*"))
                        Directory.Delete(unused,true);
            }
        }
        catch (IOException) { /* Will retry cleanup next start. */ }
        catch (UnauthorizedAccessException) { /* Will retry cleanup next start. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed=true;
        _client.CancelPendingRequests();
        _client.Dispose();
        // The last asynchronous check may still unwind through finally/Release.
        // Do not dispose its semaphore while that continuation is running.
    }
}

// A bounded decoder for the raw Snappy block format used by the GitHub
// attestations bundle_url endpoint (NOT Snappy framing or streaming format).
internal static class RawSnappy
{
    internal static byte[] Decode(ReadOnlySpan<byte> data,int maximum)
    {
        int pos=0, shift=0;
        ulong size=0;
        while (true)
        {
            if (pos>=data.Length || shift>28) throw new InvalidDataException("Invalid Snappy length.");
            var b=data[pos++];size|=(ulong)(b&127)<<shift;
            if ((b&128)==0) break;
            shift+=7;
        }
        if (size>(ulong)maximum) throw new InvalidDataException("Snappy output exceeds limit.");
        var output=new byte[(int)size];
        var outPos=0;
        while (pos<data.Length)
        {
            int tag=data[pos++], kind=tag&3, length, offset;
            if (kind==0)
            {
                length=tag>>2;
                if (length<60) length++;
                else
                {
                    var count=length-59;
                    if (count>4 || pos+count>data.Length) throw new InvalidDataException("Snappy literal header.");
                    length=0;
                    for (var i=0;i<count;i++) length|=data[pos++]<<(8*i);
                    length=checked(length+1);
                }
                if (length<0 || length>output.Length-outPos || length>data.Length-pos)
                    throw new InvalidDataException("Snappy literal exceeds bounds.");
                data.Slice(pos,length).CopyTo(output.AsSpan(outPos,length));
                pos+=length;outPos+=length;
                continue;
            }
            if (kind==1)
            {
                length=4+((tag>>2)&7);
                if (pos>=data.Length) throw new InvalidDataException("Snappy copy header.");
                offset=((tag&0xE0)<<3)|data[pos++];
            }
            else
            {
                length=1+(tag>>2);
                var count=kind==2?2:4;
                if (pos+count>data.Length) throw new InvalidDataException("Snappy copy offset.");
                long value=0;
                for (var i=0;i<count;i++) value|=(long)data[pos++]<<(8*i);
                if (value>int.MaxValue) throw new InvalidDataException("Snappy offset overflow.");
                offset=(int)value;
            }
            if (offset<=0 || offset>outPos || length>output.Length-outPos)
                throw new InvalidDataException("Snappy invalid copy reference.");
            for (var i=0;i<length;i++) output[outPos+i]=output[outPos+i-offset];
            outPos+=length;
        }
        if (outPos!=output.Length) throw new InvalidDataException("Truncated Snappy data.");
        return output;
    }
}
