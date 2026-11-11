using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KeyPassXCCloudSync;

// Official Proton Drive CLI bridge. No undocumented API, authentication stays in Proton CLI's OS secret store.
// Output/command structure verified against ProtonDriveApps/sdk CLI sources (Oct 2026).
public sealed class ProtonDriveStore : IRemoteStore
{
    private readonly Config _cfg;
    private readonly string _exe;
    private readonly string _root;
    public ProtonDriveStore(Config cfg)
    {
        _cfg=cfg;
        if(string.IsNullOrWhiteSpace(cfg.ProtonExecutable))
            throw new FileNotFoundException("Official Proton Drive CLI not found beside the application or on PATH; select the executable in settings");
        _exe=Path.GetFullPath(cfg.ProtonExecutable);
        if(!File.Exists(_exe))throw new FileNotFoundException("Official Proton Drive CLI not found",_exe);
        _root=cfg.ProtonRootPath.TrimEnd('/');
        if(_root!=ProtonConfiguration.Root)
            throw new SyncSafetyException("Proton root must be /my-files");
    }
    private static string[] Components(string path)
    {
        var parts=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length==0 || parts.Any(p=>p is ".." or "." || p.Contains('\\') || p.Contains(':')))
            throw new SyncSafetyException("Unsafe Proton remote path");
        return parts;
    }
    private string PathFor(string key)=>_root+"/"+string.Join("/",Components(key));
    private async Task<string> RunAsync(CancellationToken ct, params string[] args)
    {
        var stdout=await PlatformServices.Current.Cli.RunProtonAsync(
            _exe,_cfg.OperationTimeoutSec,ct,args);
        if(stdout.Length>12_000_000)
            throw new SyncSafetyException("Proton CLI JSON output unexpectedly large");
        return stdout;
    }
    private sealed record Node(string Name,string Type,DateTimeOffset Modified);
    private static string? NameOf(JsonElement item)
    {
        if(!item.TryGetProperty("name",out var n))return null;
        if(n.ValueKind==JsonValueKind.String)return n.GetString();
        if(n.ValueKind==JsonValueKind.Object && n.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.True &&
            n.TryGetProperty("value",out var v))return v.GetString();
        return null;
    }
    private async Task<IReadOnlyList<Node>> ListFolderAsync(string fullPath,CancellationToken ct)
    {
        var output=await RunAsync(ct,"filesystem","list",fullPath,"--json");
        using var doc=JsonDocument.Parse(output);
        if(doc.RootElement.ValueKind!=JsonValueKind.Array)
            throw new SyncSafetyException("Unexpected Proton Drive CLI JSON listing");
        var nodes=new List<Node>();
        foreach(var item in doc.RootElement.EnumerateArray())
        {
            var name=NameOf(item);
            if(string.IsNullOrWhiteSpace(name))throw new SyncSafetyException("Proton CLI returned unreadable encrypted name");
            var type=item.GetProperty("type").GetString()??"";
            var modified=default(DateTimeOffset);
            if(item.TryGetProperty("modificationTime",out var m)&&m.ValueKind==JsonValueKind.String)
                DateTimeOffset.TryParse(m.GetString(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out modified);
            nodes.Add(new Node(name,type,modified));
        }
        return nodes;
    }
    // Resolve the entire path by listing verified parent folders, so a missing path is not guessed from CLI stderr.
    private async Task<bool> ExistsAsync(string fullPath,bool wantFolder,CancellationToken ct)
    {
        if(fullPath=="/my-files")return wantFolder;
        if(!fullPath.StartsWith("/my-files/",StringComparison.Ordinal))throw new SyncSafetyException("Proton path outside /my-files");
        var parts=fullPath[10..].Split('/',StringSplitOptions.RemoveEmptyEntries);
        string parent="/my-files";
        for(int i=0;i<parts.Length;i++)
        {
            var nodes=await ListFolderAsync(parent,ct);
            var found=nodes.Where(x=>x.Name==parts[i]).ToArray();
            if(found.Length>1)throw new SyncSafetyException("Ambiguous Proton filename; refusing to choose");
            if(found.Length==0)return false;
            var folder=i<parts.Length-1 || wantFolder;
            if(folder && found[0].Type!="folder")throw new SyncSafetyException("Proton path component is not a folder");
            if(!folder && found[0].Type!="file")throw new SyncSafetyException("Proton path is not a regular file");
            parent+="/"+parts[i];
        }
        return true;
    }
    public async Task<bool> DownloadAsync(string path,string destination,CancellationToken ct)
    {
        var remotePath=PathFor(path);
        if(!await ExistsAsync(remotePath,false,ct))return false;
        var downloadDir=Path.Combine(Path.GetTempPath(),"kps-proton-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadDir);
        try
        {
            await RunAsync(ct,"filesystem","download",remotePath,downloadDir,"--json","--file-conflict-strategy","skip");
            var actual=Path.Combine(downloadDir,Components(path)[^1]);
            if(!File.Exists(actual))throw new SyncSafetyException("Proton CLI reported completion without downloaded file");
            File.Move(actual,destination); // Only move a completed download into engine's private staging path.
            return true;
        }
        finally { try { Directory.Delete(downloadDir,true); }catch{ } }
    }
    public async Task UploadAsync(string source,string path,WriteMode mode,CancellationToken ct)
    {
        var parts=Components(path);
        string parent=_root+"/"+string.Join("/",parts[..^1]);
        await EnsureDirectoryAsync(string.Join("/",parts[..^1]),ct);
        if(mode==WriteMode.CreateNew && await ExistsAsync(PathFor(path),false,ct))
            throw new SyncSafetyException("Proton backup already exists: refusing CreateNew");
        var uploadDir=Path.Combine(Path.GetTempPath(),"kps-proton-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uploadDir);
        try
        {
            var staged=Path.Combine(uploadDir,parts[^1]);
            File.Copy(source,staged,false);
            // For replacement, create a new revision; never trash the old main file.
            // For CreateNew, forcing skip prevents an interactive prompt if another process creates it concurrently.
            var strategy=mode==WriteMode.Replace?"create-new-revision":"skip";
            var json=await RunAsync(ct,"filesystem","upload",staged,parent,"--json","--file-conflict-strategy",strategy,"--skip-thumbnails");
            // A skipped upload has exit 0; engine's independent SHA readback validates real bytes.
            // For immutable backups, do not trust a skip: reject any pre-existing path before and after upload.
            using var report=JsonDocument.Parse(json);
            if(report.RootElement.ValueKind!=JsonValueKind.Object ||
               !report.RootElement.TryGetProperty("failedItems",out var failures) ||
               !report.RootElement.TryGetProperty("transferredItems",out var transferred) ||
               failures.GetInt32()!=0 || (mode==WriteMode.CreateNew && transferred.GetInt32()!=1))
                throw new SyncSafetyException("Proton CLI transfer was not confirmed");
        }
        finally { try { Directory.Delete(uploadDir,true); }catch{ } }
    }
    public async Task EnsureDirectoryAsync(string directory,CancellationToken ct)
    {
        var segments=Components(directory);
        var parent=_root;
        // The CLI root is fixed. Create only KeePassSync-owned folders below it.
        foreach(var segment in segments)
        {
            var children=await ListFolderAsync(parent,ct);
            var matches=children.Where(x=>x.Name==segment).ToArray();
            if(matches.Length>1)throw new SyncSafetyException("Duplicate Proton folder name");
            if(matches.Length==1)
            {
                if(matches[0].Type!="folder")throw new SyncSafetyException("Proton parent is a file");
            }
            else
            {
                await RunAsync(ct,"filesystem","create-folder",parent,segment,"--json");
                var verified=await ListFolderAsync(parent,ct);
                if(verified.Count(x=>x.Name==segment&&x.Type=="folder")!=1)
                    throw new SyncSafetyException("Proton folder creation could not be verified");
            }
            parent+="/"+segment;
        }
    }
    public async Task<IReadOnlyList<RemoteFile>> ListAsync(string directory,CancellationToken ct)
    {
        var full=PathFor(directory);
        if(!await ExistsAsync(full,true,ct))return Array.Empty<RemoteFile>();
        return (await ListFolderAsync(full,ct)).Where(x=>x.Type=="file")
            .Select(x=>new RemoteFile(directory+"/"+x.Name,x.Modified)).ToArray();
    }
    public async Task DeleteAsync(string path,CancellationToken ct)
    {
        var full=PathFor(path);
        if(!await ExistsAsync(full,false,ct))throw new SyncSafetyException("Proton backup vanished before cleanup");
        var output=await RunAsync(ct,"filesystem","trash",full,"--json");
        using var doc=JsonDocument.Parse(output);
        if(doc.RootElement.ValueKind!=JsonValueKind.Array || doc.RootElement.GetArrayLength()!=1 ||
           !doc.RootElement[0].TryGetProperty("ok",out var ok) || ok.ValueKind!=JsonValueKind.True)
            throw new SyncSafetyException("Proton CLI did not confirm moving backup to trash");
    }
    public async Task DeleteTestDirectoryAsync(string directory,CancellationToken ct)
    {
        if(!IntegrationIsolation.IsTestRunRoot(directory))
            throw new SyncSafetyException("Unsafe integration test cleanup path");
        var full=PathFor(directory);
        if(!await ExistsAsync(full,true,ct))return;
        var output=await RunAsync(ct,"filesystem","trash",full,"--json");
        using var doc=JsonDocument.Parse(output);
        if(doc.RootElement.ValueKind!=JsonValueKind.Array || doc.RootElement.GetArrayLength()!=1 ||
           !doc.RootElement[0].TryGetProperty("ok",out var ok) || ok.ValueKind!=JsonValueKind.True)
            throw new SyncSafetyException("Proton CLI did not confirm trashing integration test folder");
    }
    public void Dispose(){ }
}
