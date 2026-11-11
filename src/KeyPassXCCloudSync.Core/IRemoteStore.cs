namespace KeyPassXCCloudSync;

public enum WriteMode { Replace, CreateNew }

public sealed record RemoteFile(string Path, DateTimeOffset ModifiedUtc);

// Download returns false ONLY if the requested file definitively does not exist.
// A timeout, 401/403, 429, server failure or ambiguous response MUST throw.
public interface IRemoteStore : IDisposable
{
    Task<bool> DownloadAsync(string path, string destination, CancellationToken ct);
    Task UploadAsync(string source, string path, WriteMode mode, CancellationToken ct);
    Task<IReadOnlyList<RemoteFile>> ListAsync(string directory, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    // Tests only: delete the isolated, random, test-owned run folder and its contents.
    Task DeleteTestDirectoryAsync(string directory, CancellationToken ct);
    Task EnsureDirectoryAsync(string directory, CancellationToken ct);
}

public sealed class SyncSafetyException(string message) : Exception(message);

// The token MUST be read from the same remote object's revision before and after
// downloading the working KDBX. UploadConditionalAsync must enforce the observed
// revision atomically at the server, not perform a client-side check then PUT.
// null means the working file did not exist at scan time.
public interface IConditionalWriteStore
{
    Task<string?> ReadTokenAsync(string path,CancellationToken ct);
    Task UploadConditionalAsync(string source,string path,string? observedToken,CancellationToken ct);
}
