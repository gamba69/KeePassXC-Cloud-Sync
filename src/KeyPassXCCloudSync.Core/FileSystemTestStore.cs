namespace KeyPassXCCloudSync;

// Development-only fake transport for exercising the protocol with local files.
// Never register this provider in production; tests exercise the real SyncEngine through this interface.
public sealed class FileSystemTestStore(string root) : IRemoteStore, IConditionalWriteStore
{
    private string PathFor(string key)
    {
        var parts=key.Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length==0 || parts.Any(x=>x is ".." or "." || x.Contains('\\') || x.Contains(':')))
            throw new SyncSafetyException("Unsafe remote key");
        return Path.Combine([Path.GetFullPath(root),..parts]);
    }
    public Task<bool> DownloadAsync(string path,string destination,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var source=PathFor(path);
        if(!File.Exists(source)) return Task.FromResult(false);
        File.Copy(source,destination);
        return Task.FromResult(true);
    }
    public Task<string?> ReadTokenAsync(string path,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var file=PathFor(path);
        return Task.FromResult(File.Exists(file)?Hashes.Sha256(file):null);
    }
    public Task UploadConditionalAsync(string source,string path,string? observedToken,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target=PathFor(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // The fake backend uses a cooperating cross-process lock for deterministic CAS tests.
        // Real providers must implement this with their own server-side preconditions.
        using var locked=new FileStream(target+".cas-lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var actual=File.Exists(target)?Hashes.Sha256(target):null;
        if(actual!=observedToken)throw new SyncSafetyException("Simulated CAS rejected concurrent write");
        var tmp=target+"."+Guid.NewGuid().ToString("N")+".pending";
        try
        {
            File.Copy(source,tmp,false);
            File.Move(tmp,target,true);
        }
        finally{if(File.Exists(tmp))File.Delete(tmp);}
        return Task.CompletedTask;
    }
    public Task UploadAsync(string source,string path,WriteMode mode,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dest=PathFor(path);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source,dest,mode==WriteMode.Replace);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<RemoteFile>> ListAsync(string directory,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var p=PathFor(directory);
        IReadOnlyList<RemoteFile> items=Directory.Exists(p)
            ? Directory.GetFiles(p).Select(f=>new RemoteFile(directory+"/"+Path.GetFileName(f),File.GetLastWriteTimeUtc(f))).ToArray()
            : Array.Empty<RemoteFile>();
        return Task.FromResult(items);
    }
    public Task DeleteAsync(string path,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        File.Delete(PathFor(path));
        return Task.CompletedTask;
    }
    public Task DeleteTestDirectoryAsync(string directory,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IntegrationIsolation.IsTestRunRoot(directory)) throw new SyncSafetyException("Unsafe integration test cleanup path");
        var path=PathFor(directory);
        if(Directory.Exists(path)) Directory.Delete(path,true);
        return Task.CompletedTask;
    }
    public Task EnsureDirectoryAsync(string directory,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(PathFor(directory));
        return Task.CompletedTask;
    }
    public void Dispose(){ }
}
