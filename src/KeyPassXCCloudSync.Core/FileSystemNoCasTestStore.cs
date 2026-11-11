namespace KeyPassXCCloudSync;

// Isolated test transport that deliberately has NO server-side compare-and-swap.
// It models the unconditional working-file replacement used with Google Drive.
// Never expose this provider outside KPS_ENABLE_TEST_STORE=1.
internal sealed class FileSystemNoCasTestStore(string root) : IRemoteStore
{
    private readonly FileSystemTestStore _inner=new(root);
    public Task<bool> DownloadAsync(string path,string destination,CancellationToken ct)
        =>_inner.DownloadAsync(path,destination,ct);
    public Task UploadAsync(string source,string path,WriteMode mode,CancellationToken ct)
        =>_inner.UploadAsync(source,path,mode,ct);
    public Task<IReadOnlyList<RemoteFile>> ListAsync(string directory,CancellationToken ct)
        =>_inner.ListAsync(directory,ct);
    public Task DeleteAsync(string path,CancellationToken ct)=>_inner.DeleteAsync(path,ct);
    public Task DeleteTestDirectoryAsync(string directory,CancellationToken ct)
        =>_inner.DeleteTestDirectoryAsync(directory,ct);
    public Task EnsureDirectoryAsync(string directory,CancellationToken ct)
        =>_inner.EnsureDirectoryAsync(directory,ct);
    public void Dispose()=>_inner.Dispose();
}
